/// Turns the CI part of the plan into federated credentials on the CI identities: which GitHub OIDC
/// subjects may get an Azure token as id-platform-apply or id-platform-preview.
module Platform.Access.CiFederation

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative.ManagedIdentity
open Platform.Access.People
open Platform.Access.Desired

let private credential
    (subscriptionId: string)
    (resourceGroup: string)
    (after: Resource list)
    (c: FederatedCredential)
    =
    let identity = CiIdentity.name c.identity

    let id =
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.ManagedIdentity"
        + $"/userAssignedIdentities/{identity}/federatedIdentityCredentials/{c.name}"

    // Credentials bootstrap.sh made are imported (their inputs must match exactly), not created.
    let options =
        if c.existing then
            CustomResourceOptions(ImportId = id, DependsOn = inputList (after |> List.map input))
        else
            CustomResourceOptions(DependsOn = inputList (after |> List.map input))

    FederatedIdentityCredential(
        $"{identity}-{c.name}",
        FederatedIdentityCredentialArgs(
            ResourceGroupName = resourceGroup,
            ResourceName = identity,
            FederatedIdentityCredentialResourceName = c.name,
            Issuer = "https://token.actions.githubusercontent.com",
            Subject = c.subject,
            Audiences = inputList [ input "api://AzureADTokenExchange" ]
        ),
        options
    )

/// Azure refuses concurrent credential writes on one identity, so each identity's credentials are made
/// one after another; the two identities proceed in parallel.
let deploy (subscriptionId: string) (resourceGroup: string) (plan: Plan) : FederatedIdentityCredential list =
    plan.ciCredentials
    |> List.groupBy _.identity
    |> List.collect (fun (_, credentials) ->
        credentials
        |> List.fold
            (fun made c ->
                credential subscriptionId resourceGroup (made |> List.map (fun r -> r :> Resource)) c
                :: made)
            []
        |> List.rev)

/// The apply identity's client id, as a repo variable, for every platform job that may sign in as it (the `dev`
/// and `teardown` environments). An identifier, not a credential: the federated credentials decide who gets in.
let applyClientId (resourceGroup: string) =
    let identity =
        GetUserAssignedIdentity.Invoke(
            GetUserAssignedIdentityInvokeArgs(
                ResourceGroupName = resourceGroup,
                ResourceName = CiIdentity.name ApplyIdentity
            )
        )

    Pulumi.Github.ActionsVariable(
        "platform-AZURE_CLIENT_ID",
        Pulumi.Github.ActionsVariableArgs(
            Repository = RepoName.value RepoName.platform,
            VariableName = "AZURE_CLIENT_ID",
            Value = io (identity.Apply(_.ClientId))
        )
    )
