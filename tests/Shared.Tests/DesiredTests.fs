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
