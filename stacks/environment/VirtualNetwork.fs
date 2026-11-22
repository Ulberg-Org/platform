/// The environment's network (D86): the database accepts only Postgres from the nodes; the nodes, from the
/// internet, only the gateway's ports to its static address. Everything else inbound is Azure's default deny.
module Platform.Environment.VirtualNetwork

open Pulumi
open Pulumi.FSharp
open Pulumi.AzureNative
open Platform.Environment.Desired

type Deployed =
    {
        nodeSubnet: Network.Subnet
        databaseSubnet: Network.Subnet
        zone: PrivateDns.PrivateZone
        zoneLink: PrivateDns.VirtualNetworkLink
    }

let private rule (name: string) (priority: int) (source: string) (destination: string) (port: string) =
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

let private denyRest =
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

let deploy (stack: Stack) (network: Desired.Network) : Deployed =
    let environment = stack.environment
    let groupName = input stack.names.group

    let databaseRules =
        Network.NetworkSecurityGroup(
            $"nsg-{environment}-database",
            Network.NetworkSecurityGroupArgs(
                ResourceGroupName = groupName,
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
                ResourceGroupName = groupName,
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
                ResourceGroupName = groupName,
                AddressSpace = Network.Inputs.AddressSpaceArgs(AddressPrefixes = inputList [ input network.vnet ])
            )
        )

    let nodeSubnet =
        Network.Subnet(
            "nodes",
            Network.SubnetArgs(
                ResourceGroupName = groupName,
                VirtualNetworkName = vnet.Name,
                SubnetName = "nodes",
                AddressPrefix = network.nodes,
                NetworkSecurityGroup = Network.Inputs.NetworkSecurityGroupArgs(Id = nodeRules.Id),
                // The environment's Event Hub admits the nodes through it (D97: Alloy reads it in Phase 5).
                ServiceEndpoints =
                    inputList
                        [
                            input (Network.Inputs.ServiceEndpointPropertiesFormatArgs(Service = "Microsoft.EventHub"))
                        ]
            )
        )

    let databaseSubnet =
        Network.Subnet(
            "database",
            Network.SubnetArgs(
                ResourceGroupName = groupName,
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
                ResourceGroupName = groupName,
                PrivateZoneName = stack.names.databaseZone,
                Location = "Global"
            )
        )

    let zoneLink =
        PrivateDns.VirtualNetworkLink(
            "database-zone-link",
            PrivateDns.VirtualNetworkLinkArgs(
                ResourceGroupName = groupName,
                PrivateZoneName = zone.Name,
                Location = "Global",
                VirtualNetwork = PrivateDns.Inputs.SubResourceArgs(Id = vnet.Id),
                RegistrationEnabled = false
            )
        )

    {
        nodeSubnet = nodeSubnet
        databaseSubnet = databaseSubnet
        zone = zone
        zoneLink = zoneLink
    }
