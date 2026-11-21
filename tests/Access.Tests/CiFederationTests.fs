module Platform.Access.Tests.CiFederationTests

open Expecto
open Platform.Access.Desired
open Platform.Access.Tests.Fixtures

let private credentials = federatedCredentials ciTrusts

let private subjectsOf identity =
    credentials
    |> List.filter (fun c -> c.identity = identity)
    |> List.map (fun c -> c.name, c.subject)

[<Tests>]
let tests =
    testList
        "CI federated credentials"
        [
            test "the apply identity trusts only approved runs in dev, and teardowns" {
                Expect.equal
                    (subjectsOf ApplyIdentity)
                    [
                        "github", "repo:Owner@1/platform@9:environment:dev"
                        "github-teardown", "repo:Owner@1/platform@9:environment:teardown"
                        "github-org", "repo:Org@2/platform@9:environment:dev"
                        "github-org-teardown", "repo:Org@2/platform@9:environment:teardown"
                    ]
                    "apply subjects"
            }

            test "the preview identity trusts pull requests and main" {
                Expect.equal
                    (subjectsOf PreviewIdentity)
                    [
                        "github", "repo:Owner@1/platform@9:pull_request"
                        "github-main", "repo:Owner@1/platform@9:ref:refs/heads/main"
                        "github-org", "repo:Org@2/platform@9:pull_request"
                        "github-org-main", "repo:Org@2/platform@9:ref:refs/heads/main"
                    ]
                    "preview subjects"
            }

            test "the bootstrap's credentials are imported under their existing names; the rest are created" {
                Expect.equal
                    (credentials
                     |> List.filter _.existing
                     |> List.map (fun c -> CiIdentity.name c.identity, c.name))
                    [
                        "id-platform-apply", "github"
                        "id-platform-preview", "github"
                        "id-platform-preview", "github-main"
                    ]
                    "imported"
            }

            test "an empty list, a bad name or a prefix with a claim in it is refused" {
                Expect.isError (CiTrust.parse "[]") "empty"

                Expect.isError
                    (CiTrust.parse """[ { "name": "x", "repoPrefix": "repo:Org@2/platform@9" } ]""")
                    "name too short"

                Expect.isError
                    (CiTrust.parse """[ { "name": "github", "repoPrefix": "repo:Org/platform:ref:refs/heads/x" } ]""")
                    "prefix includes a claim"
            }
        ]
