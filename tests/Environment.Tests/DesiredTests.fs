module Platform.Environment.Tests.DesiredTests

open Expecto
open Platform.Environment.Desired

let private cidr text =
    match Cidr.parse text with
    | Ok c -> c
    | Error e -> failwith e

[<Tests>]
let networkTests =
    testList
        "environment: network"
        [
            test "a range contains the ranges inside it, and no others" {
                Expect.isTrue (Cidr.contains (cidr "10.20.0.0/16") (cidr "10.20.2.0/24")) "inside"
                Expect.isFalse (Cidr.contains (cidr "10.20.0.0/16") (cidr "10.21.0.0/24")) "outside"
            }

            test "a range that isn't an IPv4 CIDR is rejected" {
                Expect.isError (Cidr.parse "10.20.0.0") "no prefix"
                Expect.isError (Cidr.parse "10.20.0.300/16") "not an address"
            }

            test "dev's layout is accepted: both subnets inside the VNet, apart" {
                Expect.isOk (Network.parse "10.20.0.0/16" "10.20.0.0/23" "10.20.2.0/24") "dev (D70, D86)"
            }

            test "a subnet outside the VNet, or two that overlap, are rejected" {
                Expect.isError (Network.parse "10.20.0.0/16" "10.20.0.0/23" "10.30.2.0/24") "database outside"
                Expect.isError (Network.parse "10.20.0.0/16" "10.20.0.0/23" "10.20.1.0/24") "overlapping"
            }
        ]

[<Tests>]
let accessTests =
    testList
        "environment: who reaches the cluster (D84, D88)"
        [
            test "only platform-write may change the cluster" {
                let admins =
                    Cluster.access
                    |> List.filter (fun (_, roles) -> List.contains RbacClusterAdmin roles)
                    |> List.map fst

                Expect.equal admins [ "platform-write" ] "admins"
            }

            test "readers and every Claude agent read, and get the kubeconfig to do so" {
                for group in [ "platform-read"; "platform-agents" ] do
                    Expect.equal (Map.find group (Map.ofList Cluster.access)) [ ClusterUser; RbacReader ] group
            }

            test "app developers have nothing in the cluster" {
                Expect.isFalse
                    (Cluster.access |> List.exists (fun (group, _) -> group.StartsWith "app-"))
                    "no app groups"
            }
        ]

[<Tests>]
let nameTests =
    testList
        "environment: names"
        [
            test "everything is named after the environment" {
                let n = Names.forEnvironment "ulberg" "dev"
                Expect.equal n.group "rg-dev" "group"
                Expect.equal n.cluster "aks-dev" "cluster"
                Expect.equal n.database "psql-ulberg-dev" "database (globally unique)"
                Expect.equal n.databaseZone "dev.private.postgres.database.azure.com" "private DNS zone"
            }
        ]

[<Tests>]
let databaseTests =
    testList
        "environment: an app's database (D69, D75, D85)"
        [
            let todo =
                {
                    name = "todo"
                    app = "todo"
                    service = "backend"
                }

            test "the backend's identity and service account follow the app, the service and the environment" {
                Expect.equal (AppDatabase.identity "dev" todo) "id-todo-backend-dev" "identity"
                Expect.equal (AppDatabase.serviceAccount todo) "system:serviceaccount:todo:backend" "subject"
            }

            test "the roles are the shared SQL, then Azure's, filled in for the app" {
                let sql =
                    AppDatabase.rolesSql "CREATE ROLE @db@_app NOLOGIN;" "GRANT @db@_app TO \"@backend@\";" "dev" todo

                Expect.equal sql "CREATE ROLE todo_app NOLOGIN;\nGRANT todo_app TO \"id-todo-backend-dev\";" "filled"
            }
        ]

[<Tests>]
let ciTests =
    testList
        "environment: CI in the cluster"
        [
            test "the apply identity administers the cluster (it runs the bootstrap); the preview identity only reads" {
                Expect.equal (Map.find "apply" (Map.ofList Cluster.ciAccess)) [ ClusterUser; RbacClusterAdmin ] "apply"
                Expect.equal (Map.find "preview" (Map.ofList Cluster.ciAccess)) [ ClusterUser; RbacReader ] "preview"
            }

            test "CI's kubeconfig signs in through kubelogin as the az session, and holds no credential" {
                let config = Cluster.kubeconfig "aks-dev.hcp.example" "Q0E="

                Expect.stringContains config "server: https://aks-dev.hcp.example:443" "server"
                Expect.stringContains config "certificate-authority-data: Q0E=" "CA"
                Expect.stringContains config "command: kubelogin" "kubelogin"
                Expect.stringContains config "- azurecli" "as the az session"
                Expect.isFalse (config.Contains "token:") "no token"
            }
        ]
