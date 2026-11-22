module Platform.Access.Tests.EnvironmentTests

open Expecto
open Platform.Access
open Platform.Access.Desired

let private rolesOf grantee =
    Cluster.access |> Map.ofList |> Map.find grantee

[<Tests>]
let environmentTests =
    testList
        "access: each environment's resource group (D95)"
        [
            test "is read from the environment stack's config: its name and location" {
                let yaml =
                    "config:\n  azure-native:location: swedencentral\n  environment:nodeCount: \"2\"\n"

                Expect.equal
                    (AzureEnvironment.parse "Pulumi.dev.yaml" yaml)
                    (Ok
                        {
                            name = "dev"
                            location = "swedencentral"
                        })
                    "dev"

                Expect.equal
                    (AzureEnvironment.group
                        {
                            name = "dev"
                            location = "swedencentral"
                        })
                    "rg-dev"
                    "group"
            }

            test "a stack config without a location is refused" {
                Expect.isError (AzureEnvironment.parse "Pulumi.prod.yaml" "config: {}\n") "no location"
            }
        ]

[<Tests>]
let clusterTests =
    testList
        "access: who reaches the clusters (D84, D88)"
        [
            test "only platform-write and CI's apply identity may change a cluster" {
                let admins =
                    Cluster.access
                    |> List.filter (fun (_, roles) -> List.contains RbacClusterAdmin roles)
                    |> List.map fst

                Expect.equal admins [ ClusterGrantee.Group PlatformWrite; ClusterGrantee.Ci ApplyIdentity ] "admins"
            }

            test "readers, every Claude agent and CI's preview identity read, and get the kubeconfig to do so" {
                for grantee in
                    [
                        ClusterGrantee.Group PlatformRead
                        ClusterGrantee.Group PlatformAgents
                        ClusterGrantee.Ci PreviewIdentity
                    ] do
                    Expect.equal (rolesOf grantee) [ ClusterUser; RbacReader ] (string grantee)
            }

            test "app developers and their agents have nothing in the cluster" {
                Expect.isFalse
                    (Cluster.access
                     |> List.exists (fun (grantee, _) ->
                         match grantee with
                         | ClusterGrantee.Group group -> (GroupName.value group).StartsWith "app-"
                         | ClusterGrantee.Ci _ -> false))
                    "no app groups"
            }
        ]
