/// Each environment's permanent resource group, and who reaches the clusters in it (D84, D88, D95). The
/// environment stack fills the group and empties it again; the group and its access stay.
module Platform.Access.Environments

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative
open Platform.Access.Desired

let private principal (ciResourceGroup: string) (groups: Map<GroupName, Pulumi.AzureAD.Group>) =
    function
    | ClusterGrantee.Group group -> GroupName.value group, io groups[group].ObjectId, Authorization.PrincipalType.Group
    | ClusterGrantee.Ci identity ->
        let name = CiIdentity.name identity

        let principalId =
            ManagedIdentity.GetUserAssignedIdentity
                .Invoke(
                    ManagedIdentity.GetUserAssignedIdentityInvokeArgs(
                        ResourceGroupName = ciResourceGroup,
                        ResourceName = name
                    )
                )
                .Apply
                _.PrincipalId

        name, io principalId, Authorization.PrincipalType.ServicePrincipal

let private environment
    (subscriptionId: string)
    (ciResourceGroup: string)
    (groups: Map<GroupName, Pulumi.AzureAD.Group>)
    (environment: AzureEnvironment)
    =
    let groupName = AzureEnvironment.group environment

    let roles =
        $"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions"

    let group =
        Resources.ResourceGroup(
            groupName,
            Resources.ResourceGroupArgs(ResourceGroupName = groupName, Location = environment.location),
            // The environment stack sets and removes the `started` tag (D45).
            CustomResourceOptions(IgnoreChanges = ResizeArray [ "tags" ])
        )

    Cluster.access
    |> List.collect (fun (grantee, clusterRoles) ->
        let name, principalId, principalType = principal ciResourceGroup groups grantee

        clusterRoles
        |> List.map (fun clusterRole ->
            Authorization.RoleAssignment(
                $"{groupName}-{name}-{(string clusterRole).ToLowerInvariant()}",
                Authorization.RoleAssignmentArgs(
                    Scope = group.Id,
                    RoleDefinitionId = $"{roles}/{ClusterRole.definitionId clusterRole}",
                    PrincipalId = principalId,
                    PrincipalType = principalType,
                    Description = $"{name}: {clusterRole} on the clusters in {groupName} (D88, D95)"
                )
            )))

/// The role assignments, on every environment's group.
let deploy subscriptionId ciResourceGroup groups (environments: AzureEnvironment list) =
    environments |> List.collect (environment subscriptionId ciResourceGroup groups)
