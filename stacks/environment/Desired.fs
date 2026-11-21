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

/// The cluster's roles that people and agents get, through their Entra groups (D84, D88).
type ClusterRole =
    /// Lets them fetch the kubeconfig at all; what they may do is decided by the RBAC roles.
    | ClusterUser
    | RbacReader
    | RbacClusterAdmin

module ClusterRole =
    let definitionId =
        function
        | ClusterUser -> "4abbcc35-e782-43d8-92c5-2d3f1bd2253f"
        | RbacReader -> "7f6c6a51-bcf8-42ba-9220-52d62157d7db"
        | RbacClusterAdmin -> "b1ff04bb-8a4e-4dc4-8eb5-8693973ce19b"

module Cluster =
    /// Readers and every Claude agent read (no Secrets); only platform-write, through PIM, changes anything.
    /// App developers have nothing in the cluster (AGENTS.md §3).
    let access =
        [
            "platform-read", [ ClusterUser; RbacReader ]
            "platform-agents", [ ClusterUser; RbacReader ]
            "platform-write", [ ClusterUser; RbacClusterAdmin ]
        ]

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
    }

module Names =
    /// `prefix` makes the database's name globally unique.
    let forEnvironment (prefix: string) (environment: string) =
        {
            group = $"rg-{environment}"
            nodeGroup = $"rg-{environment}-nodes"
            cluster = $"aks-{environment}"
            clusterIdentity = $"id-aks-{environment}"
            database = $"psql-{prefix}-{environment}"
            databaseAdmin = $"id-db-admin-{environment}"
            databaseZone = $"{environment}.private.postgres.database.azure.com"
        }
