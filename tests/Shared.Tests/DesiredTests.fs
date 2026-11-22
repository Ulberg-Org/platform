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

            test "every Azure service a stack uses is registered by code, not by hand" {
                for used in
                    [
                        "Microsoft.Security"
                        "Microsoft.EventHub"
                        "Microsoft.Insights"
                        "Microsoft.DBforPostgreSQL"
                    ] do
                    Expect.contains ResourceProviders.all used used
            }
        ]

[<Tests>]
let importTests =
    testList
        "shared: images copied into the registry (D100)"
        [
            test "a pinned release is copied by digest, under our name, keeping its tag" {
                let digest = "sha256:" + String.replicate 64 "a"

                Expect.equal
                    (ImportedImage.parse "platform/renderer" $"ghcr.io/org/platform-renderer:v0.2.4@{digest}")
                    (Ok
                        {
                            source = $"ghcr.io/org/platform-renderer@{digest}"
                            target = "platform/renderer:v0.2.4"
                            digest = digest
                        })
                    "pinned"
            }

            test "an image without its digest is refused: only the signed bytes may be copied" {
                Expect.isError
                    (ImportedImage.parse "platform/renderer" "ghcr.io/org/platform-renderer:v0.2.4")
                    "no digest"
            }
        ]

[<Tests>]
let rendererPinTests =
    testList
        "shared: the renderer Argo CD runs in Azure"
        [
            test "values-dev.yaml names the copy shared imports: the same digest (D100)" {
                let root = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..")

                let read (path: string) =
                    System.IO.File.ReadAllText(System.IO.Path.Combine(root, path))

                let digest (text: string) =
                    System.Text.RegularExpressions.Regex
                        .Match(text, @"platform-?/?renderer[^\s@]*@(sha256:[0-9a-f]{64})")
                        .Groups[1].Value

                let pinned = digest (read "stacks/shared/Pulumi.main.yaml")
                Expect.isNotEmpty pinned "shared:renderer is pinned by digest"
                Expect.equal (digest (read "components/argocd/values-dev.yaml")) pinned "values-dev.yaml"
            }
        ]
