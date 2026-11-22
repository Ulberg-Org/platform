/// The environment's Postgres (D85): private, Entra only; its Entra admin is a managed identity, which the roles
/// Job uses. The settings the platform owns (D93), and each app's database with its service's identity.
module Platform.Environment.Postgres

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative
open Platform.Environment.Desired

/// The server's version and size.
type Settings = { version: string; sku: string }

type Deployed =
    {
        admin: ManagedIdentity.UserAssignedIdentity
        adminFederation: ManagedIdentity.FederatedIdentityCredential
        server: DBforPostgreSQL.Server
        administrator: DBforPostgreSQL.Administrator
        restart: Pulumi.Command.Local.Command
        /// Each app's database.
        databases: (AppDatabase * DBforPostgreSQL.Database) list
    }

/// `issuer`: the cluster's OIDC issuer, which the pods' identities trust (workload identity, D69);
/// `adminAccount`: the Kubernetes service account the roles Job runs as.
let deploy
    (stack: Stack)
    (vnet: VirtualNetwork.Deployed)
    (settings: Settings)
    (databases: AppDatabase list)
    (issuer: Output<string>)
    (adminAccount: string)
    : Deployed =
    let environment = stack.environment
    let names = stack.names
    let groupName = input names.group

    let admin =
        ManagedIdentity.UserAssignedIdentity(
            names.databaseAdmin,
            ManagedIdentity.UserAssignedIdentityArgs(ResourceGroupName = groupName, ResourceName = names.databaseAdmin)
        )

    let server =
        DBforPostgreSQL.Server(
            names.database,
            DBforPostgreSQL.ServerArgs(
                ResourceGroupName = groupName,
                ServerName = names.database,
                Version = settings.version,
                Sku = DBforPostgreSQL.Inputs.SkuArgs(Name = settings.sku, Tier = DBforPostgreSQL.SkuTier.Burstable),
                Storage = DBforPostgreSQL.Inputs.StorageArgs(StorageSizeGB = 32),
                Network =
                    DBforPostgreSQL.Inputs.NetworkArgs(
                        DelegatedSubnetResourceId = vnet.databaseSubnet.Id,
                        PrivateDnsZoneArmResourceId = vnet.zone.Id,
                        PublicNetworkAccess = DBforPostgreSQL.ServerPublicNetworkAccessState.Disabled
                    ),
                AuthConfig =
                    DBforPostgreSQL.Inputs.AuthConfigArgs(
                        ActiveDirectoryAuth = DBforPostgreSQL.MicrosoftEntraAuth.Enabled,
                        PasswordAuth = DBforPostgreSQL.PasswordBasedAuth.Disabled,
                        TenantId = stack.tenantId
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
            CustomResourceOptions(DependsOn = inputList [ input (vnet.zoneLink :> Resource) ])
        )

    let administrator =
        DBforPostgreSQL.Administrator(
            $"{names.database}-admin",
            DBforPostgreSQL.AdministratorArgs(
                ResourceGroupName = groupName,
                ServerName = server.Name,
                ObjectId = admin.PrincipalId,
                PrincipalName = admin.Name,
                PrincipalType = DBforPostgreSQL.PrincipalType.ServicePrincipal,
                TenantId = stack.tenantId
            ),
            // Goes with the server: dropped on its own, Postgres refuses (it granted the roles' memberships).
            CustomResourceOptions(DeletedWith = server)
        )

    // The settings the platform owns (D93), one after another (ServerSettings), then a restart: Postgres reads
    // shared_preload_libraries only at start, and Pulumi can't restart a server, so az does, as CI's apply
    // identity, once per server and preload. Azure runs one operation on a server at a time and refuses the rest,
    // so everything done to it is one chain: its admin, the settings, the restart, the apps' databases.
    let settingsApplied =
        ServerSettings.all
        |> List.fold
            (fun (previous: Resource list) (name, value) ->
                [
                    DBforPostgreSQL.Configuration(
                        $"{names.database}-{name}",
                        DBforPostgreSQL.ConfigurationArgs(
                            ResourceGroupName = groupName,
                            ServerName = server.Name,
                            ConfigurationName = name,
                            Value = value,
                            Source = "user-override"
                        ),
                        CustomResourceOptions(DeletedWith = server, DependsOn = inputList (List.map input previous))
                    )
                    :> Resource
                ])
            [ administrator :> Resource ]

    let restart =
        Pulumi.Command.Local.Command(
            $"{names.database}-restart",
            Pulumi.Command.Local.CommandArgs(
                Create = "az postgres flexible-server restart --resource-group \"$GROUP\" --name \"$SERVER\"",
                Environment = inputMap [ "GROUP", input names.group; "SERVER", input names.database ],
                Triggers = inputList [ io (server.Id.Apply box); input (box ServerSettings.preload) ]
            ),
            CustomResourceOptions(DeletedWith = server, DependsOn = inputList (List.map input settingsApplied))
        )

    // Identities that pods use (workload identity, D69): trusted for one Kubernetes service account each.
    let federate (identity: ManagedIdentity.UserAssignedIdentity) (identityName: string) (subject: string) =
        ManagedIdentity.FederatedIdentityCredential(
            $"{identityName}-aks",
            ManagedIdentity.FederatedIdentityCredentialArgs(
                ResourceGroupName = groupName,
                ResourceName = identity.Name,
                FederatedIdentityCredentialResourceName = "aks",
                Issuer = io issuer,
                Subject = subject,
                Audiences = inputList [ input "api://AzureADTokenExchange" ]
            )
        )

    // Each app's database, and its service's identity, which the database knows by name (D69, D85).
    let appDatabases =
        databases
        |> List.map (fun d ->
            let identityName = AppDatabase.identity environment d

            let identity =
                ManagedIdentity.UserAssignedIdentity(
                    identityName,
                    ManagedIdentity.UserAssignedIdentityArgs(
                        ResourceGroupName = groupName,
                        ResourceName = identityName
                    )
                )

            federate identity identityName (AppDatabase.serviceAccount d) |> ignore

            d,
            DBforPostgreSQL.Database(
                $"{names.database}-{d.name}",
                DBforPostgreSQL.DatabaseArgs(
                    ResourceGroupName = groupName,
                    ServerName = server.Name,
                    DatabaseName = d.name,
                    Charset = "UTF8",
                    Collation = "en_US.utf8"
                ),
                CustomResourceOptions(DeletedWith = server, DependsOn = inputList [ input (restart :> Resource) ])
            ))

    {
        admin = admin
        adminFederation = federate admin names.databaseAdmin adminAccount
        server = server
        administrator = administrator
        restart = restart
        databases = appDatabases
    }
