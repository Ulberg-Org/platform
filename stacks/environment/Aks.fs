/// The environment's AKS (D84): Entra only, workload identity, the nodes in the environment's network; and the
/// Kubernetes provider Pulumi reaches it with.
module Platform.Environment.Aks

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative
open Platform.Environment.Desired

/// The cluster's size and version, and what it uses in `shared`: its gateway's address and the registry.
type Settings =
    {
        kubernetesVersion: string
        nodeCount: int
        nodeSize: string
        address: string
        registry: string
    }

type Deployed =
    {
        cluster: ContainerService.ManagedCluster
        acrPull: Authorization.RoleAssignment
        kubernetes: Pulumi.Kubernetes.Provider
    }

let deploy (stack: Stack) (vnet: VirtualNetwork.Deployed) (settings: Settings) : Deployed =
    let environment = stack.environment
    let names = stack.names
    let groupName = input names.group

    // Its control plane's identity may join the node subnet and use the static address in shared.
    let clusterIdentity =
        ManagedIdentity.UserAssignedIdentity(
            names.clusterIdentity,
            ManagedIdentity.UserAssignedIdentityArgs(
                ResourceGroupName = groupName,
                ResourceName = names.clusterIdentity
            )
        )

    let clusterNetwork =
        [ "nodes", vnet.nodeSubnet.Id; "address", Output.Create settings.address ]
        |> List.map (fun (name, scope) ->
            Authorization.RoleAssignment(
                $"aks-{environment}-network-{name}",
                Authorization.RoleAssignmentArgs(
                    Scope = scope,
                    RoleDefinitionId = Stack.role stack AzureRole.networkContributor,
                    PrincipalId = clusterIdentity.PrincipalId,
                    PrincipalType = Authorization.PrincipalType.ServicePrincipal,
                    Description = $"AKS {environment}: its nodes' subnet and its gateway's address"
                )
            ))

    let pool =
        ContainerService.Inputs.ManagedClusterAgentPoolProfileArgs(
            Name = "system",
            Mode = ContainerService.AgentPoolMode.System,
            Count = settings.nodeCount,
            VmSize = settings.nodeSize,
            OsType = ContainerService.OSType.Linux,
            VnetSubnetID = vnet.nodeSubnet.Id,
            EnableNodePublicIP = false
        )

    let cluster =
        ContainerService.ManagedCluster(
            names.cluster,
            ContainerService.ManagedClusterArgs(
                ResourceGroupName = groupName,
                ResourceName = names.cluster,
                NodeResourceGroup = names.nodeGroup,
                DnsPrefix = names.cluster,
                KubernetesVersion = settings.kubernetesVersion,
                Sku =
                    ContainerService.Inputs.ManagedClusterSKUArgs(
                        Name = ContainerService.ManagedClusterSKUName.Base,
                        Tier = ContainerService.ManagedClusterSKUTier.Free
                    ),
                Identity =
                    ContainerService.Inputs.ManagedClusterIdentityArgs(
                        Type = ContainerService.ResourceIdentityType.UserAssigned,
                        UserAssignedIdentities = inputList [ io clusterIdentity.Id ]
                    ),
                AgentPoolProfiles = inputList [ input pool ],
                NetworkProfile =
                    ContainerService.Inputs.ContainerServiceNetworkProfileArgs(
                        NetworkPlugin = ContainerService.NetworkPlugin.Azure,
                        NetworkPluginMode = ContainerService.NetworkPluginMode.Overlay,
                        NetworkDataplane = ContainerService.NetworkDataplane.Cilium,
                        NetworkPolicy = ContainerService.NetworkPolicy.Cilium,
                        LoadBalancerSku = ContainerService.LoadBalancerSku.Standard,
                        OutboundType = ContainerService.OutboundType.LoadBalancer
                    ),
                // Entra only: Azure RBAC decides, and there are no local accounts.
                AadProfile =
                    ContainerService.Inputs.ManagedClusterAADProfileArgs(
                        Managed = true,
                        EnableAzureRBAC = true,
                        TenantID = stack.tenantId
                    ),
                DisableLocalAccounts = true,
                OidcIssuerProfile = ContainerService.Inputs.ManagedClusterOIDCIssuerProfileArgs(Enabled = true),
                SecurityProfile =
                    ContainerService.Inputs.ManagedClusterSecurityProfileArgs(
                        WorkloadIdentity =
                            ContainerService.Inputs.ManagedClusterSecurityProfileWorkloadIdentityArgs(Enabled = true)
                    )
            ),
            CustomResourceOptions(DependsOn = inputList (clusterNetwork |> List.map (fun r -> input (r :> Resource))))
        )

    // The nodes pull images from shared's registry.
    let acrPull =
        Authorization.RoleAssignment(
            $"aks-{environment}-acr-pull",
            Authorization.RoleAssignmentArgs(
                Scope = settings.registry,
                RoleDefinitionId = Stack.role stack AzureRole.registryReader,
                PrincipalId =
                    io (
                        cluster.IdentityProfile.Apply(fun profile -> (nonNull profile).Item("kubeletidentity").ObjectId)
                    ),
                PrincipalType = Authorization.PrincipalType.ServicePrincipal,
                Description = $"AKS {environment}'s nodes pull images"
            )
        )

    // The cluster, for Pulumi: its address and CA, and kubelogin as the job's az session (Cluster.kubeconfig).
    let caData =
        ContainerService.ListManagedClusterUserCredentials
            .Invoke(
                ContainerService.ListManagedClusterUserCredentialsInvokeArgs(
                    ResourceGroupName = groupName,
                    ResourceName = cluster.Name
                )
            )
            .Apply(fun c ->
                let config =
                    System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String c.Kubeconfigs[0].Value)

                System.Text.RegularExpressions.Regex
                    .Match(config, "certificate-authority-data: (\\S+)")
                    .Groups[1].Value)

    let kubeconfig =
        Output.Tuple(cluster.Fqdn, caData).Apply(fun struct (fqdn, ca) -> Cluster.kubeconfig fqdn ca)

    {
        cluster = cluster
        acrPull = acrPull
        kubernetes =
            Pulumi.Kubernetes.Provider(
                $"aks-{environment}",
                Pulumi.Kubernetes.ProviderArgs(KubeConfig = io kubeconfig, DeleteUnreachable = true)
            )
    }
