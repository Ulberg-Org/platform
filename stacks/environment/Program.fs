/// An environment stack (D45, D82–D88): its network, AKS and Postgres, applied by the `start` workflow and
/// destroyed after 4 hours. What outlives it (its address, the registry) is in `shared`; its resource group and
/// the access on it are the access stack's (D95). As a pipeline: input → plan (Desired) → action (one module per
/// area) → report.
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
        |> Option.defaultWith (fun _ -> raise (RunException $"environment: {name} isn't set"))

    let subscriptionId = required "ARM_SUBSCRIPTION_ID"

    let stack =
        {
            environment = environment
            names = Names.forEnvironment (config.Require "namePrefix") environment
            subscription = $"/subscriptions/{subscriptionId}"
            tenantId = required "ARM_TENANT_ID"
        }

    let sharedGroupName = config.Require "sharedGroup"
    let registryName = config.Require "registryName"
    let sharedGroup = $"{stack.subscription}/resourceGroups/{sharedGroupName}"
    let databases = config.RequireObject<AppDatabase list> "databases"
    // The database's roles: the same SQL as locally (D85), then Azure's sign-ins, run by azure-roles.sh.
    let databaseFile name =
        System.IO.File.ReadAllText(
            System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "..", "..", "database", name)
        )

    let cluster: Aks.Settings =
        {
            kubernetesVersion = config.Require "kubernetesVersion"
            nodeCount = config.RequireInt32 "nodeCount"
            nodeSize = config.Require "nodeSize"
            address = $"{sharedGroup}/providers/Microsoft.Network/publicIPAddresses/pip-{environment}"
            registry = $"{sharedGroup}/providers/Microsoft.ContainerRegistry/registries/{registryName}"
        }

    let bootstrap: Bootstrap.Settings =
        {
            ns = "platform-db"
            image = config.Require "rolesImage"
            rolesSql = databaseFile "roles.sql"
            azureSql = databaseFile "azure.sql"
            rolesScript = databaseFile "azure-roles.sh"
        }

    // 2. Plan
    match Network.parse (config.Require "vnet") (config.Require "nodes") (config.Require "database") with
    | Error problems ->
        problems |> List.iter (fun p -> Log.Error p)
        raise (RunException $"environment: {List.length problems} problem(s), see the errors above")
    | Ok network ->
        // 3. Action
        // `started` on the group tells `destroy` when the environment last started (D45, D99): every `pulumi up`
        // moves it, so dev lives 4 hours after the latest merge; removed with the environment.
        let started = System.DateTime.UtcNow.ToString "yyyy-MM-ddTHH:mm:ssZ"

        Resources.TagAtScope(
            $"{stack.names.group}-started",
            Resources.TagAtScopeArgs(
                Scope = $"{stack.subscription}/resourceGroups/{stack.names.group}",
                Properties = Resources.Inputs.TagsArgs(Tags = inputMap [ "started", input started ])
            )
        )
        |> ignore

        let vnet = VirtualNetwork.deploy stack network
        let aks = Aks.deploy stack vnet cluster

        let postgres =
            Postgres.deploy
                stack
                vnet
                {
                    version = config.Require "postgresVersion"
                    sku = config.Require "postgresSku"
                }
                databases
                (aks.cluster.OidcIssuerProfile.Apply(_.IssuerURL))
                $"system:serviceaccount:{bootstrap.ns}:{Bootstrap.account}"

        let signals = AzureSignals.deploy stack vnet postgres.server
        let rolesJobs = Bootstrap.deploy stack aks postgres bootstrap
        let argocd = Bootstrap.argocd stack aks (config.Require "repo")

        // 4. Report
        Log.Info(
            $"environment {environment}: VNet {network.vnet} (nodes {network.nodes}, database {network.database}), "
            + $"AKS {stack.names.cluster} ({cluster.nodeCount} × {cluster.nodeSize}), Event Hubs {stack.names.eventHubs} "
            + $"({List.length Telemetry.activityLog} Activity Log categories and the server's logs), "
            + $"""Postgres {stack.names.database} ({databases |> List.map _.name |> String.concat ", "}, {rolesJobs.Length} roles Job(s))"""
        )

        dict
            [
                "cluster", aks.cluster.Name :> obj
                "oidcIssuer", aks.cluster.OidcIssuerProfile.Apply(_.IssuerURL) :> obj
                "database", postgres.server.FullyQualifiedDomainName :> obj
                "databaseAdminClientId", postgres.admin.ClientId :> obj
                "nodesPull", aks.acrPull.Id :> obj
                "eventHubs", signals.hubs.Name :> obj
                "serverLogs", signals.serverLogs.Id :> obj
                "activityLog", signals.activityLog.Id :> obj
                "argocd", argocd.Stdout :> obj
            ]

[<EntryPoint>]
let main _ =
    Deployment.RunAsync(run).GetAwaiter().GetResult()
