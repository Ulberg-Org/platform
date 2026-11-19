/// Every repo's committed CODEOWNERS must give the whole repo to its leads' team. It is committed by hand,
/// not pushed by a bot, because a bot pushing to main would need a ruleset bypass.
module Platform.Access.Codeowners

open Platform.Access.People
open Platform.Access.Desired

/// The file every repo must contain. Who is in the team is people.yaml's repo-leads (Desired.teams).
let expected (org: GitHubHandle) (repo: RepoName) =
    $"# Generated from the platform repo's access/people.yaml (repo-leads). Change it there, not here.\n"
    + $"* @{GitHubHandle.value org}/{Team.leadsOf repo}\n"

/// Compares what each repo has committed with what it should contain. Pure: the reading is done by the caller.
let check (org: GitHubHandle) (committed: (RepoName * string option) list) : Result<unit, string list> =
    committed
    |> List.choose (fun (repo, actual) ->
        let want = expected org repo

        if actual = Some want then
            None
        else
            Some $"{RepoName.value repo}: .github/CODEOWNERS must be exactly:\n{want}")
    |> function
        | [] -> Ok()
        | errors -> Error errors
