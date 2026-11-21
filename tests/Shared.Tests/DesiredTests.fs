module Platform.Shared.Tests.DesiredTests

open Expecto
open Platform.Shared.Desired

let private todo =
    {
        app = "todo"
        repoSubject = "repo:Org@1/todo@2"
    }

[<Tests>]
let tests =
    testList
        "shared: registry access"
        [
            test "an app's CI pushes only from main" {
                Expect.equal (AppRegistryAccess.subject todo) "repo:Org@1/todo@2:ref:refs/heads/main" "subject"
            }

            test "the repository role applies only inside the app's own repositories" {
                let condition = repositoryCondition (AppRegistryAccess.repositoryPrefix todo)

                Expect.stringContains
                    condition
                    "@Request[Microsoft.ContainerRegistry/registries/repositories:name] StringStartsWithIgnoreCase 'todo/'"
                    "limited to todo/"

                for action in [ "content/read"; "content/write"; "metadata/read"; "metadata/write" ] do
                    Expect.stringContains
                        condition
                        $"!(ActionMatches{{'Microsoft.ContainerRegistry/registries/repositories/{action}'}})"
                        action
            }
        ]

[<Tests>]
let environmentTests =
    testList
        "shared: what every environment keeps"
        [
            test "an environment's address is a DNS label from the prefix and its name" {
                Expect.equal (Environment.dnsLabel "ulberg" "dev") "ulberg-dev" "label"
            }

            test "only known environment names are accepted" {
                Expect.equal (Environment.parse [ "dev"; "prod" ]) (Ok [ "dev"; "prod" ]) "dev and prod"
                Expect.isError (Environment.parse [ "dev"; "staging" ]) "staging isn't an environment"
            }

            test "telemetry keeps 14 days, in one container per store" {
                Expect.equal Telemetry.retentionDays 14 "days"
                Expect.equal Telemetry.containers [ "loki"; "tempo" ] "containers"
            }

            test "the budget warns at half, most and all of it" {
                Expect.equal Budget.thresholds [ 50; 80; 100 ] "thresholds"
            }
        ]
