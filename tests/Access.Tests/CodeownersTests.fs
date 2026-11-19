module Platform.Access.Tests.CodeownersTests

open System.IO
open Expecto
open Platform.Access
open Platform.Access.People
open Platform.Access.Tests.Fixtures

[<Tests>]
let tests =
    let org = github "Org"

    testList
        "CODEOWNERS"
        [
            test "gives the whole repo to its leads' team" {
                Expect.stringEnds (Codeowners.expected org (repo "todo")) "\n* @Org/todo-leads\n" "owners line"
            }

            test "a matching file passes" {
                Expect.equal
                    (Codeowners.check org [ repo "todo", Some(Codeowners.expected org (repo "todo")) ])
                    (Ok())
                    "ok"
            }

            test "a missing or different file names the repo and the expected content" {
                let result =
                    Codeowners.check org [ repo "todo", None; repo "platform", Some "* @someone" ]

                match result with
                | Error [ todo; platform ] ->
                    Expect.stringStarts todo "todo: .github/CODEOWNERS must be exactly" "todo"
                    Expect.stringStarts platform "platform:" "platform"
                | other -> failtestf "expected two errors, got %A" other
            }

            test "the committed platform CODEOWNERS names the stack's organization" {
                let config =
                    File.ReadAllText(Path.Combine(platformRoot, "stacks", "access", "Pulumi.main.yaml"))

                let org =
                    config.Split '\n'
                    |> Array.find (fun l -> l.Trim().StartsWith "github:owner:")
                    |> fun l -> (l.Split(':', 3)[2]).Trim()
                    |> github

                let committed =
                    File.ReadAllText(Path.Combine(platformRoot, ".github", "CODEOWNERS"))

                Expect.equal committed (Codeowners.expected org RepoName.platform) "platform CODEOWNERS"
            }
        ]
