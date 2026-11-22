/// The shared stack (D53): everything persistent that environments use and never destroy: the container
/// registry (D54: Basic, Entra-only, repository permissions; each app's CI may push only into its own
/// repositories, only from `main`), each environment's address, telemetry storage and the budget (D89).
/// As a pipeline: input → plan (Desired) → action → report.
module Platform.Shared.Program

open System.IO
open System.Text.RegularExpressions
open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative
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

let private problemsOf result =
    match result with
    | Ok _ -> []
    | Error problems -> problems

let private run () =
    // 1. Input
    let config = Config "shared"
    let azure = Config "azure-native"
    let subscriptionId = azure.Require "subscriptionId"
    let tenantId = azure.Require "tenantId"
    let registryName = config.Require "registryName"
    let dnsPrefix = config.Require "dnsPrefix"
    let telemetryAccountName = config.Require "telemetryAccountName"
    let budget = config.RequireDouble "budgetMonthly"
    // The alert list (D40) is a CI secret, kept out of the public repo.
    let alertEmails =
        (System.Environment.GetEnvironmentVariable "PLATFORM_ALERT_EMAILS"
         |> Option.ofObj
         |> Option.defaultValue "")
            .Split(
                ',',
                System.StringSplitOptions.RemoveEmptyEntries
                ||| System.StringSplitOptions.TrimEntries
            )
        |> List.ofArray

    let repositoryPermissionsScript =
        File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "scripts", "acr-repository-permissions.sh"))

    // 2. Plan
    let apps = config.RequireObject<AppDto list> "apps" |> parseApps

    let environments =
        config.RequireObject<string list> "environments" |> Environment.parse

    let recipients =
        match alertEmails with
        | [] -> Error "PLATFORM_ALERT_EMAILS is empty: the budget alert (D89) would reach nobody"
        | emails -> Ok emails

    match apps, environments, recipients with
    | Ok apps, Ok environments, Ok alertEmails ->
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

        // Each environment's address (D45): a static IP and DNS label that outlive the environment.
        let addresses =
            environments
            |> List.map (fun environment ->
                environment,
                Network.PublicIPAddress(
                    $"pip-{environment}",
                    Network.PublicIPAddressArgs(
                        ResourceGroupName = group.Name,
                        PublicIpAddressName = $"pip-{environment}",
                        Sku = Network.Inputs.PublicIPAddressSkuArgs(Name = Network.PublicIPAddressSkuName.Standard),
                        PublicIPAllocationMethod = Network.IPAllocationMethod.Static,
                        DnsSettings =
                            Network.Inputs.PublicIPAddressDnsSettingsArgs(
                                DomainNameLabel = Environment.dnsLabel dnsPrefix environment
                            )
                    )
                ))

        // Telemetry storage (D89): Entra-only (no account keys), nothing public, kept 14 days.
        let telemetry =
            Storage.StorageAccount(
                "telemetry",
                Storage.StorageAccountArgs(
                    ResourceGroupName = group.Name,
                    AccountName = telemetryAccountName,
                    Kind = Storage.Kind.StorageV2,
                    Sku = Storage.Inputs.SkuArgs(Name = Storage.SkuName.Standard_LRS),
                    AllowBlobPublicAccess = false,
                    AllowSharedKeyAccess = false,
                    MinimumTlsVersion = Storage.MinimumTlsVersion.TLS1_2,
                    EnableHttpsTrafficOnly = true
                )
            )

        for container in Telemetry.containers do
            Storage.BlobContainer(
                $"telemetry-{container}",
                Storage.BlobContainerArgs(
                    ResourceGroupName = group.Name,
                    AccountName = telemetry.Name,
                    ContainerName = container,
                    PublicAccess = Storage.PublicAccess.None
                )
            )
            |> ignore

        let deleteAfter =
            Storage.Inputs.ManagementPolicyBaseBlobArgs(
                Delete =
                    Storage.Inputs.DateAfterModificationArgs(
                        DaysAfterModificationGreaterThan = float Telemetry.retentionDays
                    )
            )

        let retention =
            Storage.Inputs.ManagementPolicyRuleArgs(
                Name = "retention",
                Enabled = true,
                Type = Storage.RuleType.Lifecycle,
                Definition =
                    Storage.Inputs.ManagementPolicyDefinitionArgs(
                        Filters = Storage.Inputs.ManagementPolicyFilterArgs(BlobTypes = inputList [ input "blockBlob" ]),
                        Actions = Storage.Inputs.ManagementPolicyActionArgs(BaseBlob = deleteAfter)
                    )
            )

        Storage.ManagementPolicy(
            "telemetry-retention",
            Storage.ManagementPolicyArgs(
                ResourceGroupName = group.Name,
                AccountName = telemetry.Name,
                ManagementPolicyName = "default",
                Policy = Storage.Inputs.ManagementPolicySchemaArgs(Rules = inputList [ input retention ])
            )
        )
        |> ignore

        // The monthly budget (D89), in the subscription's billing currency, to the alert list (D40).
        let notification (threshold: int) =
            CostManagement.Inputs.NotificationArgs(
                Enabled = true,
                Operator = CostManagement.BudgetNotificationOperatorType.GreaterThanOrEqualTo,
                Threshold = float threshold,
                ThresholdType = CostManagement.ThresholdType.Actual,
                ContactEmails = inputList (alertEmails |> List.map input)
            )

        CostManagement.Budget(
            "budget",
            CostManagement.BudgetArgs(
                Scope = $"/subscriptions/{subscriptionId}",
                BudgetName = "platform-monthly",
                Amount = budget,
                Category = CostManagement.CategoryType.Cost,
                TimeGrain = CostManagement.TimeGrainType.Monthly,
                TimePeriod = CostManagement.Inputs.BudgetTimePeriodArgs(StartDate = "2026-11-01T00:00:00Z"),
                Notifications =
                    inputMap
                        [
                            for threshold in Budget.thresholds -> $"actual{threshold}", input (notification threshold)
                        ]
            )
        )
        |> ignore

        // Defender for open-source databases (D93, D97): the subscription's plan, so every Postgres server is covered
        // (dev now, prod later), billed per server-hour. Its alerts reach our telemetry through the Activity Log.
        let defender =
            Security.Pricing(
                "defender-open-source-databases",
                Security.PricingArgs(
                    ScopeId = $"/subscriptions/{subscriptionId}",
                    PricingName = "OpenSourceRelationalDatabases",
                    PricingTier = Security.PricingTier.Standard
                )
            )

        // 4. Report
        Log.Info(
            $"shared: registry {registryName} (Basic, Entra-only, repository permissions), {apps.Length} app pusher(s); "
            + $"""addresses for {String.concat ", " environments}; telemetry storage {telemetryAccountName}; budget {budget}/month; """
            + "Defender for open-source databases"
        )

        dict
            [
                "registryLoginServer", registry.LoginServer :> obj
                yield!
                    pushers
                    |> List.map (fun p -> $"{p.app}PushClientId", p.identity.ClientId :> obj)
                yield!
                    addresses
                    |> List.map (fun (environment, ip) -> $"{environment}Ip", ip.IpAddress :> obj)
                "telemetryAccount", telemetry.Name :> obj
                "defenderOpenSourceDatabases", defender.PricingTier :> obj
            ]
    | _ ->
        let problems =
            problemsOf apps
            @ problemsOf (Result.mapError List.singleton environments)
            @ problemsOf (Result.mapError List.singleton recipients)

        problems |> List.iter (fun p -> Log.Error p)
        raise (RunException $"shared: {List.length problems} problem(s), see the errors above")

[<EntryPoint>]
let main _ =
    Deployment.RunAsync(run).GetAwaiter().GetResult()
