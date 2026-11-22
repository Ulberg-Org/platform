/// What an environment stack should hold (D82–D88): pure data and pure functions, so every rule here is
/// unit-tested; Program.fs only turns it into resources.
module Platform.Environment.Desired

/// An IPv4 range.
type Cidr = { first: uint32; prefix: int }

module Cidr =
    let private octets (address: string) =
        match address.Split '.' |> Array.map System.Byte.TryParse with
        | [| (true, a); (true, b); (true, c); (true, d) |] ->
            Some((uint32 a <<< 24) ||| (uint32 b <<< 16) ||| (uint32 c <<< 8) ||| uint32 d)
        | _ -> None

    let parse (text: string) : Result<Cidr, string> =
        match text.Split '/' with
        | [| address; prefix |] ->
            match octets address, System.Int32.TryParse prefix with
            | Some first, (true, bits) when bits >= 0 && bits <= 32 -> Ok { first = first; prefix = bits }
            | _ -> Error $"'{text}' is not an IPv4 range"
        | _ -> Error $"'{text}' is not an IPv4 range (a.b.c.d/n)"

    let private last (c: Cidr) =
        c.first
        + (if c.prefix = 0 then
               System.UInt32.MaxValue
           else
               (1u <<< (32 - c.prefix)) - 1u)

    let contains (outer: Cidr) (inner: Cidr) =
        inner.first >= outer.first && last inner <= last outer

    let overlaps (a: Cidr) (b: Cidr) = a.first <= last b && b.first <= last a

/// An environment's network (D70, D86): the VNet, the AKS nodes' subnet and the database's.
type Network =
    {
        vnet: string
        nodes: string
        database: string
    }

module Network =
    let parse (vnet: string) (nodes: string) (database: string) : Result<Network, string list> =
        match Cidr.parse vnet, Cidr.parse nodes, Cidr.parse database with
        | Ok v, Ok n, Ok d ->
            let problems =
                [
                    if not (Cidr.contains v n) then
                        $"nodes {nodes} isn't inside the VNet {vnet}"
                    if not (Cidr.contains v d) then
                        $"database {database} isn't inside the VNet {vnet}"
                    if Cidr.overlaps n d then
                        $"nodes {nodes} and database {database} overlap"
                ]

            if List.isEmpty problems then
                Ok
                    {
                        vnet = vnet
                        nodes = nodes
                        database = database
                    }
            else
                Error problems
        | v, n, d ->
            Error(
                [ v; n; d ]
                |> List.choose (function
                    | Error e -> Some e
                    | Ok _ -> None)
            )

module Cluster =
    let private template =
        System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "kubeconfig.yaml"))

    /// CI's kubeconfig: the cluster's address and CA, and kubelogin for the sign-in (kubeconfig.yaml).
    let kubeconfig (server: string) (ca: string) =
        template.Replace("@server@", server).Replace("@ca@", ca)

/// Built-in roles the environment's own identities get.
module AzureRole =
    let acrPull = "7f951dda-4ed3-4680-a7ca-43fe172d538d"
    let networkContributor = "4d97b98b-1d4f-4787-a291-c67834d212e7"

/// An environment's resource names, all derived from its name.
type Names =
    {
        group: string
        nodeGroup: string
        cluster: string
        clusterIdentity: string
        database: string
        databaseAdmin: string
        databaseZone: string
        eventHubs: string
    }

module Names =
    /// `prefix` makes the database's and the Event Hubs namespace's names globally unique.
    let forEnvironment (prefix: string) (environment: string) =
        {
            group = $"rg-{environment}"
            nodeGroup = $"rg-{environment}-nodes"
            cluster = $"aks-{environment}"
            clusterIdentity = $"id-aks-{environment}"
            database = $"psql-{prefix}-{environment}"
            databaseAdmin = $"id-db-admin-{environment}"
            databaseZone = $"{environment}.private.postgres.database.azure.com"
            eventHubs = $"evhns-{prefix}-{environment}"
        }

/// What every part of an environment stack shares: which environment, its names, and where it lives (Program.fs
/// reads them from its config and CI's environment).
type Stack =
    {
        environment: string
        names: Names
        subscription: string
        tenantId: string
    }

module Stack =
    /// A built-in role's id in the subscription.
    let role (stack: Stack) (id: string) =
        $"{stack.subscription}/providers/Microsoft.Authorization/roleDefinitions/{id}"

/// The server's settings the platform owns (D92, D93), applied one at a time: Azure refuses parallel changes.
module ServerSettings =
    /// Azure preloads pg_stat_statements by default; pgaudit joins it. Read only when Postgres starts, so a change
    /// needs a restart.
    let preload = "pg_stat_statements,pgaudit"

    /// pgaudit logs role and privilege changes and DDL, each class named (Azure takes no `-class`); every
    /// connection and disconnection is logged, failed sign-ins included.
    let all =
        [
            "azure.extensions", "PGAUDIT"
            "shared_preload_libraries", preload
            "pgaudit.log", "ROLE,DDL"
            "log_connections", "on"
            "log_disconnections", "on"
        ]

/// Azure's own signals for our telemetry (D97): to the environment's Event Hubs namespace, which Alloy reads into
/// Loki in Phase 5.
module Telemetry =
    /// The subscription's Activity Log, all of it; Defender's alerts come as "Security".
    let activityLog =
        [
            "Administrative"
            "Security"
            "ServiceHealth"
            "Alert"
            "Recommendation"
            "Policy"
            "Autoscale"
            "ResourceHealth"
        ]

/// An app's database on the environment's server (D69, D85), and the service that uses it.
type AppDatabase =
    {
        name: string
        app: string
        service: string
    }

module AppDatabase =
    /// The service's workload identity, which the database knows by this name.
    let identity (environment: string) (d: AppDatabase) = $"id-{d.app}-{d.service}-{environment}"

    /// The Kubernetes service account the service runs as (the renderer's, Phase 4): namespace = app.
    let serviceAccount (d: AppDatabase) =
        $"system:serviceaccount:{d.app}:{d.service}"

    /// The database's SQL (database/roles.sql, database/azure.sql) filled in for the app.
    let fill (environment: string) (d: AppDatabase) (sql: string) =
        sql.Replace("@db@", d.name).Replace("@backend@", identity environment d)
