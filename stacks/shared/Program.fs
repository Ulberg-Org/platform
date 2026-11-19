/// The shared stack (D53): everything persistent that environments use and never destroy. Today the
/// container registry (D54): Basic, Entra-only, repository permissions; each app's CI may push only into its
/// own repositories, only from `main`. As a pipeline: input → plan (Desired) → action → report.
module Platform.Shared.Program

open System.IO
open System.Text.RegularExpressions
open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative.Resources
open Pulumi.AzureNative.ContainerRegistry
open Pulumi.AzureNative.ContainerRegistry.Inputs
open Pulumi.AzureNative.ManagedIdentity
open Pulumi.AzureNative.Authorization
open Pulumi.Command.Local
open Platform.Shared.Desired

type AppDto = { app: string; repoSubject: string }

/// What one app's push access consists of.
type Pusher =
    {
        app: string
        identity: UserAssignedIdentity
        credential: FederatedIdentityCredential
        writer: RoleAssignment
        variables: Pulumi.Github.ActionsVariable list
    }

let private appName = Regex "^[a-z][a-z0-9-]{1,30}$"
let private repoSubject = Regex "^repo:[^:\\s]+$"

/// Parses `shared:apps`. Every problem is reported, then the run fails.
let private parseApps (apps: AppDto list) : Result<AppRegistryAccess list, string list> =
    let problems =
        apps
        |> List.collect (fun a ->
            [
                if not (appName.IsMatch a.app) then
                    $"shared:apps: '{a.app}' is not a valid app name"
                if not (repoSubject.IsMatch a.repoSubject) then
                    $"shared:apps.{a.app}: '{a.repoSubject}' is not a repo:<owner>@<id>/<repo>@<id> prefix"
            ])

    match problems with
    | [] ->
        Ok(
            apps
            |> List.map (fun a ->
                {
                    app = a.app
                    repoSubject = a.repoSubject
                })
        )
    | _ -> Error problems

let private run () =
    // 1. Input
    let config = Config "shared"
    let azure = Config "azure-native"
    let subscriptionId = azure.Require "subscriptionId"
    let tenantId = azure.Require "tenantId"
    let registryName = config.Require "registryName"

    let repositoryPermissionsScript =
        File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "scripts", "acr-repository-permissions.sh"))

    // 2. Plan
    match config.RequireObject<AppDto list> "apps" |> parseApps with
    | Error problems ->
        problems |> List.iter (fun p -> Log.Error p)
        raise (RunException $"shared: {List.length problems} problem(s), see the errors above")
    | Ok apps ->
        // 3. Action
        let group =
            ResourceGroup("rg-platform-shared", ResourceGroupArgs(ResourceGroupName = "rg-platform-shared"))

        let registry =
            Registry(
                "registry",
                RegistryArgs(
                    ResourceGroupName = group.Name,
                    RegistryName = registryName,
                    Sku = SkuArgs(Name = SkuName.Basic),
                    AdminUserEnabled = false,
                    AnonymousPullEnabled = false,
                    PublicNetworkAccess = PublicNetworkAccess.Enabled
                )
            )

        // Repository permissions (ABAC), so a role can be limited to some repositories. The provider can't
        // set this mode yet, so a script does it on apply and reads it back.
        let repositoryPermissions =
            Command(
                "acr-repository-permissions",
                CommandArgs(
                    Create = repositoryPermissionsScript,
                    Interpreter = inputList [ input "/bin/bash"; input "-c" ],
                    Environment = inputMap [ "REGISTRY", io registry.Name ],
                    Triggers = inputList [ input (box repositoryPermissionsScript) ]
                ),
                CustomResourceOptions(DependsOn = inputList [ input (registry :> Pulumi.Resource) ])
            )

        let pushers =
            apps
            |> List.map (fun a ->
                let identity =
                    UserAssignedIdentity(
                        AppRegistryAccess.identityName a,
                        UserAssignedIdentityArgs(
                            ResourceGroupName = group.Name,
                            ResourceName = AppRegistryAccess.identityName a
                        )
                    )

                let credential =
                    FederatedIdentityCredential(
                        $"{AppRegistryAccess.identityName a}-github-main",
                        FederatedIdentityCredentialArgs(
                            ResourceGroupName = group.Name,
                            ResourceName = identity.Name,
                            FederatedIdentityCredentialResourceName = "github-main",
                            Issuer = "https://token.actions.githubusercontent.com",
                            Subject = AppRegistryAccess.subject a,
                            Audiences = inputList [ input "api://AzureADTokenExchange" ]
                        )
                    )

                let writer =
                    RoleAssignment(
                        $"{a.app}-acr-repository-writer",
                        RoleAssignmentArgs(
                            Scope = registry.Id,
                            RoleDefinitionId =
                                $"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{repositoryWriterRole}",
                            PrincipalId = identity.PrincipalId,
                            PrincipalType = PrincipalType.ServicePrincipal,
                            Condition = repositoryCondition (AppRegistryAccess.repositoryPrefix a),
                            ConditionVersion = "2.0",
                            Description =
                                $"{a.app} CI (main only): push and lock tags in {AppRegistryAccess.repositoryPrefix a}*"
                        ),
                        CustomResourceOptions(
                            DependsOn = inputList [ input (repositoryPermissions :> Pulumi.Resource) ]
                        )
                    )

                // The app's CI reads where and as whom to push from its repo variables (not secrets: none are).
                let variables =
                    [
                        "ACR_LOGIN_SERVER", registry.LoginServer
                        "ACR_PUSH_CLIENT_ID", identity.ClientId
                        "AZURE_TENANT_ID", Output.Create tenantId
                        "AZURE_SUBSCRIPTION_ID", Output.Create subscriptionId
                    ]
                    |> List.map (fun (name, value) ->
                        Pulumi.Github.ActionsVariable(
                            $"{a.app}-{name}",
                            Pulumi.Github.ActionsVariableArgs(
                                Repository = a.app,
                                VariableName = name,
                                Value = io value
                            )
                        ))

                {
                    app = a.app
                    identity = identity
                    credential = credential
                    writer = writer
                    variables = variables
                })

        // 4. Report
        Log.Info(
            $"shared: registry {registryName} (Basic, Entra-only, repository permissions), {apps.Length} app pusher(s)"
        )

        dict
            [
                "registryLoginServer", registry.LoginServer :> obj
                yield!
                    pushers
                    |> List.map (fun p -> $"{p.app}PushClientId", p.identity.ClientId :> obj)
            ]

[<EntryPoint>]
let main _ =
    Deployment.RunAsync(fun () -> run ()).GetAwaiter().GetResult()
