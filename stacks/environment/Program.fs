/// An environment stack (D45, D82–D88): its network, AKS and Postgres, applied by the `start` workflow and
/// destroyed after 4 hours. What outlives it (its address, the registry) is in `shared`. As a pipeline:
/// input → plan (Desired) → action → report.
module Platform.Environment.Program

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative
open Platform.Environment.Desired

let private run () =
    // 1. Input
    let environment = Deployment.Instance.StackName
    let config = Config "environment"
    // Which tenant and subscription: CI's environment (ARM_TENANT_ID, ARM_SUBSCRIPTION_ID, from the repo's
    // variables), which Azure's provider reads too; not repeated in the stack's config.
    let required name =
        System.Environment.GetEnvironmentVariable name
        |> Option.ofObj
        |> Option.defaultWith (fun () -> raise (RunException $"environment: {name} isn't set"))

    let subscriptionId = required "ARM_SUBSCRIPTION_ID"
    let tenantId = required "ARM_TENANT_ID"
    let subscription = $"/subscriptions/{subscriptionId}"
    let names = Names.forEnvironment (config.Require "namePrefix") environment
    let sharedGroupName = config.Require "sharedGroup"
    let registryName = config.Require "registryName"
    let nodeCount = config.RequireInt32 "nodeCount"
    let nodeSize = config.Require "nodeSize"
    let databases = config.RequireObject<AppDatabase list> "databases"
    let ciIdentityGroup = config.Require "ciIdentityGroup"
    let rolesImage = config.Require "rolesImage"
    let rolesNamespace = "platform-db"
    // The database's roles: the same SQL as locally (D85), then Azure's sign-ins, run by azure-roles.sh.
    let databaseFile name =
        System.IO.File.ReadAllText(
            System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "..", "..", "database", name)
        )

    let rolesSql = databaseFile "roles.sql"
    let azureSql = databaseFile "azure.sql"
    let rolesScript = databaseFile "azure-roles.sh"
    let sharedGroup = $"{subscription}/resourceGroups/{sharedGroupName}"

    let registry =
        $"{sharedGroup}/providers/Microsoft.ContainerRegistry/registries/{registryName}"

    let address =
        $"{sharedGroup}/providers/Microsoft.Network/publicIPAddresses/pip-{environment}"
    // The Entra groups' object IDs, by name, from the access stack (D88).
    let groups = StackReference "organization/access/main"

    let role id =
        $"{subscription}/providers/Microsoft.Authorization/roleDefinitions/{id}"

    // 2. Plan
    match Network.parse (config.Require "vnet") (config.Require "nodes") (config.Require "database") with
    | Error problems ->
        problems |> List.iter (fun p -> Log.Error p)
        raise (RunException $"environment: {List.length problems} problem(s), see the errors above")
    | Ok network ->
        // 3. Action
        // `started` tells `destroy` the environment's age (D45); set once, so starting it again doesn't extend it.
        let started = System.DateTime.UtcNow.ToString "yyyy-MM-ddTHH:mm:ssZ"

        let group =
            Resources.ResourceGroup(
                names.group,
                Resources.ResourceGroupArgs(
                    ResourceGroupName = names.group,
                    Tags = inputMap [ "started", input started ]
                ),
                CustomResourceOptions(IgnoreChanges = ResizeArray [ "tags" ])
            )

        // The network (D86): the database accepts only Postgres from the nodes; the nodes, from the internet,
        // only the gateway's ports to its static address. Everything else inbound is Azure's default deny.
        let rule (name: string) (priority: int) (source: string) (destination: string) (port: string) =
            Network.Inputs.SecurityRuleArgs(
                Name = name,
                Priority = priority,
                Direction = Network.SecurityRuleDirection.Inbound,
                Access = Network.SecurityRuleAccess.Allow,
                Protocol = Network.SecurityRuleProtocol.Tcp,
                SourceAddressPrefix = source,
                SourcePortRange = "*",
                DestinationAddressPrefix = destination,
                DestinationPortRange = port
            )

        let denyRest =
            Network.Inputs.SecurityRuleArgs(
                Name = "deny-other-inbound",
                Priority = 4000,
                Direction = Network.SecurityRuleDirection.Inbound,
                Access = Network.SecurityRuleAccess.Deny,
                Protocol = Network.SecurityRuleProtocol.Asterisk,
                SourceAddressPrefix = "*",
                SourcePortRange = "*",
                DestinationAddressPrefix = "*",
                DestinationPortRange = "*"
            )

        let databaseRules =
            Network.NetworkSecurityGroup(
                $"nsg-{environment}-database",
                Network.NetworkSecurityGroupArgs(
                    ResourceGroupName = group.Name,
                    SecurityRules =
                        inputList
                            [
                                input (rule "postgres-from-nodes" 100 network.nodes network.database "5432")
                                input denyRest
                            ]
                )
            )

        let nodeRules =
            Network.NetworkSecurityGroup(
                $"nsg-{environment}-nodes",
                Network.NetworkSecurityGroupArgs(
                    ResourceGroupName = group.Name,
                    SecurityRules =
                        inputList
                            [
                                input (rule "gateway-http" 100 "Internet" "*" "80")
                                input (rule "gateway-https" 110 "Internet" "*" "443")
                            ]
                )
            )

        let vnet =
            Network.VirtualNetwork(
                $"vnet-{environment}",
                Network.VirtualNetworkArgs(
                    ResourceGroupName = group.Name,
                    AddressSpace = Network.Inputs.AddressSpaceArgs(AddressPrefixes = inputList [ input network.vnet ])
                )
            )

        let nodeSubnet =
            Network.Subnet(
                "nodes",
                Network.SubnetArgs(
                    ResourceGroupName = group.Name,
                    VirtualNetworkName = vnet.Name,
                    SubnetName = "nodes",
                    AddressPrefix = network.nodes,
                    NetworkSecurityGroup = Network.Inputs.NetworkSecurityGroupArgs(Id = nodeRules.Id)
                )
            )

        let databaseSubnet =
            Network.Subnet(
                "database",
                Network.SubnetArgs(
                    ResourceGroupName = group.Name,
                    VirtualNetworkName = vnet.Name,
                    SubnetName = "database",
                    AddressPrefix = network.database,
                    NetworkSecurityGroup = Network.Inputs.NetworkSecurityGroupArgs(Id = databaseRules.Id),
                    Delegations =
                        inputList
                            [
                                input (
                                    Network.Inputs.DelegationArgs(
                                        Name = "postgres",
                                        ServiceName = "Microsoft.DBforPostgreSQL/flexibleServers"
                                    )
                                )
                            ]
                ),
                // Subnets of one VNet are changed one at a time.
                CustomResourceOptions(DependsOn = inputList [ input (nodeSubnet :> Resource) ])
            )

        // The database's private name (D85), resolvable only inside the VNet.
        let zone =
            PrivateDns.PrivateZone(
                "database-zone",
                PrivateDns.PrivateZoneArgs(
                    ResourceGroupName = group.Name,
                    PrivateZoneName = names.databaseZone,
                    Location = "Global"
                )
            )

        let zoneLink =
            PrivateDns.VirtualNetworkLink(
                "database-zone-link",
                PrivateDns.VirtualNetworkLinkArgs(
                    ResourceGroupName = group.Name,
                    PrivateZoneName = zone.Name,
                    Location = "Global",
                    VirtualNetwork = PrivateDns.Inputs.SubResourceArgs(Id = vnet.Id),
                    RegistrationEnabled = false
                )
            )

        // AKS (D84). Its control plane's identity may join the node subnet and use the static address in shared.
        let clusterIdentity =
            ManagedIdentity.UserAssignedIdentity(
                names.clusterIdentity,
                ManagedIdentity.UserAssignedIdentityArgs(
                    ResourceGroupName = group.Name,
                    ResourceName = names.clusterIdentity
                )
            )

        let clusterNetwork =
            [ "nodes", nodeSubnet.Id; "address", Output.Create address ]
            |> List.map (fun (name, scope) ->
                Authorization.RoleAssignment(
                    $"aks-{environment}-network-{name}",
                    Authorization.RoleAssignmentArgs(
                        Scope = scope,
                        RoleDefinitionId = role AzureRole.networkContributor,
                        PrincipalId = clusterIdentity.PrincipalId,
                        PrincipalType = Authorization.PrincipalType.ServicePrincipal,
                        Description = $"AKS {environment}: its nodes' subnet and its gateway's address"
                    )
                ))

        let pool =
            ContainerService.Inputs.ManagedClusterAgentPoolProfileArgs(
                Name = "system",
                Mode = ContainerService.AgentPoolMode.System,
                Count = nodeCount,
                VmSize = nodeSize,
                OsType = ContainerService.OSType.Linux,
                VnetSubnetID = nodeSubnet.Id,
                EnableNodePublicIP = false
            )

        let cluster =
            ContainerService.ManagedCluster(
                names.cluster,
                ContainerService.ManagedClusterArgs(
                    ResourceGroupName = group.Name,
                    ResourceName = names.cluster,
                    NodeResourceGroup = names.nodeGroup,
                    DnsPrefix = names.cluster,
                    KubernetesVersion = config.Require "kubernetesVersion",
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
                            TenantID = tenantId
                        ),
                    DisableLocalAccounts = true,
                    OidcIssuerProfile = ContainerService.Inputs.ManagedClusterOIDCIssuerProfileArgs(Enabled = true),
                    SecurityProfile =
                        ContainerService.Inputs.ManagedClusterSecurityProfileArgs(
                            WorkloadIdentity =
                                ContainerService.Inputs.ManagedClusterSecurityProfileWorkloadIdentityArgs(
                                    Enabled = true
                                )
                        )
                ),
                CustomResourceOptions(
                    DependsOn = inputList (clusterNetwork |> List.map (fun r -> input (r :> Resource)))
                )
            )

        // The nodes pull images from shared's registry.
        let acrPull =
            Authorization.RoleAssignment(
                $"aks-{environment}-acr-pull",
                Authorization.RoleAssignmentArgs(
                    Scope = registry,
                    RoleDefinitionId = role AzureRole.acrPull,
                    PrincipalId =
                        io (
                            cluster.IdentityProfile.Apply(fun profile ->
                                (nonNull profile).Item("kubeletidentity").ObjectId)
                        ),
                    PrincipalType = Authorization.PrincipalType.ServicePrincipal,
                    Description = $"AKS {environment}'s nodes pull images"
                )
            )

        // Who reaches the cluster, by Entra group (D84, D88).
        let clusterAccess =
            Cluster.access
            |> List.collect (fun (groupName, roles) ->
                roles
                |> List.map (fun clusterRole ->
                    Authorization.RoleAssignment(
                        $"aks-{environment}-{groupName}-{(string clusterRole).ToLowerInvariant()}",
                        Authorization.RoleAssignmentArgs(
                            Scope = cluster.Id,
                            RoleDefinitionId = role (ClusterRole.definitionId clusterRole),
                            PrincipalId = io (groups.RequireOutput(groupName).Apply(fun id -> string id)),
                            PrincipalType = Authorization.PrincipalType.Group,
                            Description = $"{groupName}: {clusterRole} on AKS {environment}"
                        )
                    )))

        // Postgres (D85): private, Entra only; its Entra admin is a managed identity, which the roles Job uses.
        let databaseAdmin =
            ManagedIdentity.UserAssignedIdentity(
                names.databaseAdmin,
                ManagedIdentity.UserAssignedIdentityArgs(
                    ResourceGroupName = group.Name,
                    ResourceName = names.databaseAdmin
                )
            )

        let database =
            DBforPostgreSQL.Server(
                names.database,
                DBforPostgreSQL.ServerArgs(
                    ResourceGroupName = group.Name,
                    ServerName = names.database,
                    Version = config.Require "postgresVersion",
                    Sku =
                        DBforPostgreSQL.Inputs.SkuArgs(
                            Name = config.Require "postgresSku",
                            Tier = DBforPostgreSQL.SkuTier.Burstable
                        ),
                    Storage = DBforPostgreSQL.Inputs.StorageArgs(StorageSizeGB = 32),
                    Network =
                        DBforPostgreSQL.Inputs.NetworkArgs(
                            DelegatedSubnetResourceId = databaseSubnet.Id,
                            PrivateDnsZoneArmResourceId = zone.Id,
                            PublicNetworkAccess = DBforPostgreSQL.ServerPublicNetworkAccessState.Disabled
                        ),
                    AuthConfig =
                        DBforPostgreSQL.Inputs.AuthConfigArgs(
                            ActiveDirectoryAuth = DBforPostgreSQL.MicrosoftEntraAuth.Enabled,
                            PasswordAuth = DBforPostgreSQL.PasswordBasedAuth.Disabled,
                            TenantId = tenantId
                        ),
                    HighAvailability =
                        DBforPostgreSQL.Inputs.HighAvailabilityArgs(
                            Mode = DBforPostgreSQL.PostgreSqlFlexibleServerHighAvailabilityMode.Disabled
                        ),
                    Backup =
                        DBforPostgreSQL.Inputs.BackupArgs(
                            BackupRetentionDays = 7,
                            GeoRedundantBackup = DBforPostgreSQL.GeographicallyRedundantBackup.Disabled
                        )
                ),
                CustomResourceOptions(DependsOn = inputList [ input (zoneLink :> Resource) ])
            )

        let administrator =
            DBforPostgreSQL.Administrator(
                $"{names.database}-admin",
                DBforPostgreSQL.AdministratorArgs(
                    ResourceGroupName = group.Name,
                    ServerName = database.Name,
                    ObjectId = databaseAdmin.PrincipalId,
                    PrincipalName = databaseAdmin.Name,
                    PrincipalType = DBforPostgreSQL.PrincipalType.ServicePrincipal,
                    TenantId = tenantId
                )
            )

        // Identities that pods use (workload identity, D69): trusted for one Kubernetes service account each.
        let issuer = cluster.OidcIssuerProfile.Apply(fun p -> p.IssuerURL)

        let federate (identity: ManagedIdentity.UserAssignedIdentity) (identityName: string) (subject: string) =
            ManagedIdentity.FederatedIdentityCredential(
                $"{identityName}-aks",
                ManagedIdentity.FederatedIdentityCredentialArgs(
                    ResourceGroupName = group.Name,
                    ResourceName = identity.Name,
                    FederatedIdentityCredentialResourceName = "aks",
                    Issuer = io issuer,
                    Subject = subject,
                    Audiences = inputList [ input "api://AzureADTokenExchange" ]
                )
            )

        let adminFederation =
            federate databaseAdmin names.databaseAdmin $"system:serviceaccount:{rolesNamespace}:db-admin"

        // Each app's database, and its service's identity, which the database knows by name (D69, D85).
        let appDatabases =
            databases
            |> List.map (fun d ->
                let identityName = AppDatabase.identity environment d

                let identity =
                    ManagedIdentity.UserAssignedIdentity(
                        identityName,
                        ManagedIdentity.UserAssignedIdentityArgs(
                            ResourceGroupName = group.Name,
                            ResourceName = identityName
                        )
                    )

                federate identity identityName (AppDatabase.serviceAccount d) |> ignore

                d,
                DBforPostgreSQL.Database(
                    $"{names.database}-{d.name}",
                    DBforPostgreSQL.DatabaseArgs(
                        ResourceGroupName = group.Name,
                        ServerName = database.Name,
                        DatabaseName = d.name,
                        Charset = "UTF8",
                        Collation = "en_US.utf8"
                    )
                ))

        // CI in the cluster: apply runs the bootstrap below, preview only reads (Cluster.ciAccess).
        let ciAccess =
            Cluster.ciAccess
            |> List.collect (fun (name, roles) ->
                let identity =
                    ManagedIdentity.GetUserAssignedIdentity.Invoke(
                        ManagedIdentity.GetUserAssignedIdentityInvokeArgs(
                            ResourceGroupName = ciIdentityGroup,
                            ResourceName = $"id-platform-{name}"
                        )
                    )

                roles
                |> List.map (fun clusterRole ->
                    Authorization.RoleAssignment(
                        $"aks-{environment}-ci-{name}-{(string clusterRole).ToLowerInvariant()}",
                        Authorization.RoleAssignmentArgs(
                            Scope = cluster.Id,
                            RoleDefinitionId = role (ClusterRole.definitionId clusterRole),
                            PrincipalId = io (identity.Apply(fun i -> i.PrincipalId)),
                            PrincipalType = Authorization.PrincipalType.ServicePrincipal,
                            Description = $"CI ({name}): {clusterRole} on AKS {environment}"
                        )
                    )))

        // The cluster, for Pulumi: its address and CA, and kubelogin as the job's az session (Cluster.kubeconfig).
        let caData =
            ContainerService.ListManagedClusterUserCredentials
                .Invoke(
                    ContainerService.ListManagedClusterUserCredentialsInvokeArgs(
                        ResourceGroupName = group.Name,
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

        let kubernetes =
            Pulumi.Kubernetes.Provider(
                $"aks-{environment}",
                Pulumi.Kubernetes.ProviderArgs(KubeConfig = io kubeconfig, DeleteUnreachable = true),
                CustomResourceOptions(DependsOn = inputList (ciAccess |> List.map (fun r -> input (r :> Resource))))
            )

        let inCluster = CustomResourceOptions(Provider = kubernetes)

        // The bootstrap (D85): a Job per database applies its roles as the server's Entra admin, once per server.
        let rolesNs =
            Pulumi.Kubernetes.Core.V1.Namespace(
                rolesNamespace,
                Pulumi.Kubernetes.Types.Inputs.Core.V1.NamespaceArgs(
                    Metadata = Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(Name = rolesNamespace)
                ),
                inCluster
            )

        let rolesAccount =
            Pulumi.Kubernetes.Core.V1.ServiceAccount(
                "db-admin",
                Pulumi.Kubernetes.Types.Inputs.Core.V1.ServiceAccountArgs(
                    Metadata =
                        Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(
                            Name = "db-admin",
                            Namespace = rolesNamespace,
                            Annotations = inputMap [ "azure.workload.identity/client-id", io databaseAdmin.ClientId ]
                        )
                ),
                CustomResourceOptions(Provider = kubernetes, DependsOn = inputList [ input (rolesNs :> Resource) ])
            )

        let rolesJobs =
            appDatabases
            |> List.map (fun (d, appDatabase) ->
                let files =
                    Pulumi.Kubernetes.Core.V1.ConfigMap(
                        $"roles-{d.name}",
                        Pulumi.Kubernetes.Types.Inputs.Core.V1.ConfigMapArgs(
                            Metadata =
                                Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(Namespace = rolesNamespace),
                            Data =
                                inputMap
                                    [
                                        "roles.sql", input (AppDatabase.rolesSql rolesSql azureSql environment d)
                                        "azure-roles.sh", input rolesScript
                                    ]
                        ),
                        CustomResourceOptions(
                            Provider = kubernetes,
                            DependsOn = inputList [ input (rolesNs :> Resource) ]
                        )
                    )

                let env (name: string) (value: Input<string>) =
                    input (Pulumi.Kubernetes.Types.Inputs.Core.V1.EnvVarArgs(Name = name, Value = value))

                let container =
                    Pulumi.Kubernetes.Types.Inputs.Core.V1.ContainerArgs(
                        Name = "roles",
                        Image = rolesImage,
                        Command = inputList [ input "/bin/sh"; input "/job/azure-roles.sh" ],
                        Env =
                            inputList
                                [
                                    env "PGHOST" (io database.FullyQualifiedDomainName)
                                    env "PGUSER" (input names.databaseAdmin)
                                    env "PGDATABASE" (input d.name)
                                    env "PGSSLMODE" (input "require")
                                ],
                        VolumeMounts =
                            inputList
                                [
                                    input (
                                        Pulumi.Kubernetes.Types.Inputs.Core.V1.VolumeMountArgs(
                                            Name = "job",
                                            MountPath = "/job"
                                        )
                                    )
                                ],
                        SecurityContext =
                            Pulumi.Kubernetes.Types.Inputs.Core.V1.SecurityContextArgs(
                                AllowPrivilegeEscalation = false,
                                ReadOnlyRootFilesystem = true,
                                Capabilities =
                                    Pulumi.Kubernetes.Types.Inputs.Core.V1.CapabilitiesArgs(
                                        Drop = inputList [ input "ALL" ]
                                    )
                            )
                    )

                let pod =
                    Pulumi.Kubernetes.Types.Inputs.Core.V1.PodSpecArgs(
                        ServiceAccountName = "db-admin",
                        RestartPolicy = "Never",
                        SecurityContext =
                            Pulumi.Kubernetes.Types.Inputs.Core.V1.PodSecurityContextArgs(
                                RunAsNonRoot = true,
                                RunAsUser = 70,
                                SeccompProfile =
                                    Pulumi.Kubernetes.Types.Inputs.Core.V1.SeccompProfileArgs(Type = "RuntimeDefault")
                            ),
                        Containers = inputList [ input container ],
                        Volumes =
                            inputList
                                [
                                    input (
                                        Pulumi.Kubernetes.Types.Inputs.Core.V1.VolumeArgs(
                                            Name = "job",
                                            ConfigMap =
                                                Pulumi.Kubernetes.Types.Inputs.Core.V1.ConfigMapVolumeSourceArgs(
                                                    Name = io (files.Metadata.Apply(fun m -> m.Name))
                                                )
                                        )
                                    )
                                ]
                    )

                Pulumi.Kubernetes.Batch.V1.Job(
                    $"roles-{d.name}",
                    Pulumi.Kubernetes.Types.Inputs.Batch.V1.JobArgs(
                        Metadata =
                            Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(
                                Namespace = rolesNamespace,
                                // A new server (each start) is a new Job; the same server keeps its roles.
                                Annotations = inputMap [ "platform/server", io database.Id ]
                            ),
                        Spec =
                            Pulumi.Kubernetes.Types.Inputs.Batch.V1.JobSpecArgs(
                                BackoffLimit = 3,
                                Template =
                                    Pulumi.Kubernetes.Types.Inputs.Core.V1.PodTemplateSpecArgs(
                                        Metadata =
                                            Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(
                                                Labels = inputMap [ "azure.workload.identity/use", input "true" ]
                                            ),
                                        Spec = pod
                                    )
                            )
                    ),
                    CustomResourceOptions(
                        Provider = kubernetes,
                        DependsOn =
                            inputList
                                [
                                    input (rolesAccount :> Resource)
                                    input (administrator :> Resource)
                                    input (adminFederation :> Resource)
                                    input (appDatabase :> Resource)
                                ]
                    )
                ))

        // 4. Report
        Log.Info(
            $"environment {environment}: VNet {network.vnet} (nodes {network.nodes}, database {network.database}), "
            + $"AKS {names.cluster} ({nodeCount} × {nodeSize}, {clusterAccess.Length} access grants), "
            + $"""Postgres {names.database} ({databases |> List.map _.name |> String.concat ", "}, {rolesJobs.Length} roles Job(s))"""
        )

        dict
            [
                "cluster", cluster.Name :> obj
                "oidcIssuer", cluster.OidcIssuerProfile.Apply(fun p -> p.IssuerURL) :> obj
                "database", database.FullyQualifiedDomainName :> obj
                "databaseAdminClientId", databaseAdmin.ClientId :> obj
                "nodesPull", acrPull.Id :> obj
            ]

[<EntryPoint>]
let main _ =
    Deployment.RunAsync(fun () -> run ()).GetAwaiter().GetResult()
