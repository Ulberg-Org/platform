/// Azure's own signals for our telemetry (D97): the environment's Event Hubs namespace, which Alloy reads into Loki
/// in Phase 5. Azure Monitor creates its hubs there itself, one per log category (insights-logs-…,
/// insights-activity-logs), so none races the namespace's provisioning. Standard, for the Kafka endpoint Alloy
/// reads. Reachable only from the nodes' subnet (its service endpoint) and by Azure's trusted services, which is
/// how Azure Monitor streams into a closed hub; Azure refuses Deny without a network rule unless public access is
/// off entirely.
module Platform.Environment.AzureSignals

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative
open Platform.Environment.Desired

type Deployed =
    {
        hubs: EventHub.Namespace
        serverLogs: Monitor.DiagnosticSetting
        activityLog: Monitor.SubscriptionDiagnosticSetting
    }

let deploy (stack: Stack) (vnet: VirtualNetwork.Deployed) (server: DBforPostgreSQL.Server) : Deployed =
    let names = stack.names
    let groupName = input names.group

    let hubs =
        EventHub.Namespace(
            names.eventHubs,
            EventHub.NamespaceArgs(
                ResourceGroupName = groupName,
                NamespaceName = names.eventHubs,
                Sku =
                    EventHub.Inputs.SkuArgs(
                        Name = EventHub.SkuName.Standard,
                        Tier = EventHub.SkuTier.Standard,
                        Capacity = 1
                    ),
                MinimumTlsVersion = "1.2"
            )
        )

    let hubNetwork =
        EventHub.NamespaceNetworkRuleSet(
            $"{names.eventHubs}-network",
            EventHub.NamespaceNetworkRuleSetArgs(
                ResourceGroupName = groupName,
                NamespaceName = hubs.Name,
                DefaultAction = EventHub.DefaultAction.Deny,
                VirtualNetworkRules =
                    inputList
                        [
                            input (
                                EventHub.Inputs.NWRuleSetVirtualNetworkRulesArgs(
                                    Subnet = EventHub.Inputs.SubnetArgs(Id = vnet.nodeSubnet.Id),
                                    IgnoreMissingVnetServiceEndpoint = false
                                )
                            )
                        ],
                TrustedServiceAccessEnabled = true,
                PublicNetworkAccess = EventHub.PublicNetworkAccessFlag.Enabled
            ),
            CustomResourceOptions(DeletedWith = hubs)
        )

    // Azure Monitor streams with the namespace's own rule (Manage, Send and Listen: Manage creates the hubs).
    let sendTo =
        hubs.Id.Apply(fun id -> $"{id}/authorizationRules/RootManageSharedAccessKey")

    // A resource's diagnostic settings outlive it and reattach to a new one with the same name (Microsoft's
    // troubleshooting docs), so a server from an earlier run can leave "telemetry" behind, which Pulumi can only
    // create. It's deleted first, whatever left it.
    let staleServerLogs =
        Pulumi.Command.Local.Command(
            $"{names.database}-telemetry-stale",
            Pulumi.Command.Local.CommandArgs(
                Create = "az monitor diagnostic-settings delete --resource \"$SERVER\" --name telemetry || true",
                Environment = inputMap [ "SERVER", io server.Id ]
            ),
            CustomResourceOptions(DeletedWith = server)
        )

    {
        hubs = hubs
        // The server's logs: pgaudit, connections, slow queries, errors.
        serverLogs =
            Monitor.DiagnosticSetting(
                $"{names.database}-telemetry",
                Monitor.DiagnosticSettingArgs(
                    Name = "telemetry",
                    ResourceUri = server.Id,
                    EventHubAuthorizationRuleId = sendTo,
                    Logs =
                        inputList
                            [
                                input (
                                    Monitor.Inputs.DiagnosticsLogSettingsArgs(CategoryGroup = "allLogs", Enabled = true)
                                )
                            ]
                ),
                CustomResourceOptions(
                    DependsOn = inputList [ input (hubNetwork :> Resource); input (staleServerLogs :> Resource) ]
                )
            )
        // The subscription's Activity Log, Defender's alerts included, while the environment runs.
        activityLog =
            Monitor.SubscriptionDiagnosticSetting(
                $"{stack.environment}-activity-log",
                Monitor.SubscriptionDiagnosticSettingArgs(
                    Name = $"telemetry-{stack.environment}",
                    EventHubAuthorizationRuleId = sendTo,
                    Logs =
                        inputList
                            [
                                for category in Telemetry.activityLog ->
                                    input (
                                        Monitor.Inputs.SubscriptionLogSettingsArgs(Category = category, Enabled = true)
                                    )
                            ]
                ),
                CustomResourceOptions(DependsOn = inputList [ input (hubNetwork :> Resource) ])
            )
    }
