/// Turns the GitHub part of a Desired.Plan into rulesets, teams, org memberships, environments and the
/// organization's defaults, and reads each repo's CODEOWNERS for the check in Codeowners.fs. No decisions
/// are made here.
module Platform.Access.GitHub

open System.IO
open System.Threading.Tasks
open Pulumi
open Pulumi.FSharp
open Pulumi.Github
open Pulumi.Github.Inputs
open Pulumi.Command.Local
open Platform.Access.People
open Platform.Access.Desired

type Deployed =
    {
        rulesets: RepositoryRuleset list
        teams: Map<string, Pulumi.Github.Team>
        teamMembers: TeamMembership list
        teamRepos: TeamRepository list
        orgMemberships: Pulumi.Github.Membership list
        environments: (RepositoryEnvironment * RepositoryEnvironmentDeploymentPolicy) list
        orgDefaults: Command
    }

/// GitHub Actions' app ID, so a required check can only be satisfied by our own workflows.
let private githubActions = 15368

let private ruleset (r: Ruleset) =
    let repo = RepoName.value r.repo

    RepositoryRuleset(
        $"{repo}-main",
        RepositoryRulesetArgs(
            Repository = repo,
            Name = "main",
            Target = "branch",
            Enforcement = "active",
            Conditions =
                RepositoryRulesetConditionsArgs(
                    RefName =
                        RepositoryRulesetConditionsRefNameArgs(
                            Includes = inputList [ input "~DEFAULT_BRANCH" ],
                            Excludes = inputList []
                        )
                ),
            // No bypass for anyone, not even org owners (D34 ended): main changes only through a PR that the
            // repo's leads team approved as code owner. Claude's app authors PRs, so a solo lead can approve.
            BypassActors = inputList [],
            Rules =
                RepositoryRulesetRulesArgs(
                    Deletion = true,
                    NonFastForward = true,
                    RequiredSignatures = true,
                    PullRequest =
                        RepositoryRulesetRulesPullRequestArgs(
                            RequiredApprovingReviewCount = 0,
                            RequireCodeOwnerReview = true,
                            DismissStaleReviewsOnPush = true,
                            RequiredReviewThreadResolution = true
                        ),
                    RequiredStatusChecks =
                        RepositoryRulesetRulesRequiredStatusChecksArgs(
                            StrictRequiredStatusChecksPolicy = false,
                            RequiredChecks =
                                inputList
                                    [
                                        for c in r.requiredChecks ->
                                            input (
                                                RepositoryRulesetRulesRequiredStatusChecksRequiredCheckArgs(
                                                    Context = c,
                                                    IntegrationId = githubActions
                                                )
                                            )
                                    ]
                        )
                )
        )
    )

let private team (t: Desired.Team) =
    Pulumi.Github.Team(
        t.slug,
        TeamArgs(Name = t.slug, Description = t.description, Privacy = "closed"),
        CustomResourceOptions(DeleteBeforeReplace = true)
    )

/// GitHub always makes organization admins maintainers of every team they're in, so they are declared as
/// maintainers; declaring "member" would be reverted by GitHub and show as a change on every run.
let private teamMember (teams: Map<string, Pulumi.Github.Team>) (orgAdmins: Person list) (t: Desired.Team) (p: Person) =
    TeamMembership(
        $"{t.slug}-{Person.key p}",
        TeamMembershipArgs(
            TeamId = teams[t.slug].Id,
            Username = GitHubHandle.value p.github,
            Role = (if List.contains p orgAdmins then "maintainer" else "member")
        )
    )

let private teamRepo
    (teams: Map<string, Pulumi.Github.Team>)
    (t: Desired.Team)
    (repo: RepoName, permission: TeamPermission)
    =
    TeamRepository(
        $"{t.slug}-{RepoName.value repo}",
        TeamRepositoryArgs(
            TeamId = teams[t.slug].Id,
            Repository = RepoName.value repo,
            Permission =
                (match permission with
                 | Push -> "push"
                 | Maintain -> "maintain")
        )
    )

/// An org membership. Never deleted from GitHub by removing it here: an admin removed by mistake would
/// lock the bootstrap owner out of the org, so leaving the org is a manual step.
let private orgMembership (m: OrgMembership) =
    Pulumi.Github.Membership(
        $"org-{Person.key m.person}",
        MembershipArgs(
            Username = GitHubHandle.value m.person.github,
            Role =
                (match m.role with
                 | OrgAdmin -> "admin"
                 | OrgMember -> "member")
        ),
        CustomResourceOptions(RetainOnDelete = true)
    )

/// An environment, managed in place (PUT is an upsert); one that existed before keeps its imported branch
/// policy. Protected, because deleting an environment also deletes its secrets.
let private environment (teams: Map<string, Pulumi.Github.Team>) (e: Environment) =
    let repo = RepoName.value e.repo

    let reviewers =
        match e.reviewers with
        | None -> inputList []
        | Some slug ->
            let ids = teams[slug].Id.Apply(int)
            inputList [ input (RepositoryEnvironmentReviewerArgs(Teams = inputList [ io ids ])) ]

    let env =
        RepositoryEnvironment(
            $"{repo}-{e.name}",
            RepositoryEnvironmentArgs(
                Repository = repo,
                Environment = e.name,
                PreventSelfReview = false,
                CanAdminsBypass = false,
                Reviewers = reviewers,
                DeploymentBranchPolicy =
                    RepositoryEnvironmentDeploymentBranchPolicyArgs(
                        ProtectedBranches = false,
                        CustomBranchPolicies = true
                    )
            ),
            CustomResourceOptions(Protect = true, RetainOnDelete = true)
        )

    let options =
        CustomResourceOptions(
            ImportId =
                (e.branchPolicyId
                 |> Option.map (fun id -> $"{repo}:{e.name}:{id}")
                 |> Option.toObj),
            DependsOn = InputList.op_Implicit (env :> Resource),
            Protect = true,
            RetainOnDelete = true
        )

    let branchPolicy =
        RepositoryEnvironmentDeploymentPolicy(
            $"{repo}-{e.name}-main",
            RepositoryEnvironmentDeploymentPolicyArgs(Repository = repo, Environment = e.name, BranchPattern = "main"),
            options
        )

    env, branchPolicy

/// The organization's defaults, set by `script` (scripts/github-org-settings.sh) on apply and read back.
/// The GitHub provider's organization-settings resource requires a billing email, which this public repo
/// shouldn't hold, so a script PATCHes only these fields.
let private orgDefaults (script: string) (org: GitHubHandle) (d: OrgDefaults) =
    let settings =
        System.Text.Json.JsonSerializer.Serialize
            {|
                default_repository_permission = d.baseRepositoryPermission
                members_can_create_repositories = d.membersCanCreateRepositories
                members_can_create_public_repositories = d.membersCanCreateRepositories
                members_can_create_private_repositories = d.membersCanCreateRepositories
                members_can_fork_private_repositories = d.membersCanForkPrivateRepositories
            |}

    Command(
        "github-org-settings",
        CommandArgs(
            Create = script,
            Interpreter = inputList [ input "/bin/bash"; input "-c" ],
            Environment = inputMap [ "ORG", input (GitHubHandle.value org); "SETTINGS", input settings ],
            Triggers = inputList [ input (box settings); input (box script) ]
        )
    )

let deploy (orgSettingsScript: string) (org: GitHubHandle) (plan: Plan) : Deployed =
    let teams = plan.teams |> List.map (fun t -> t.slug, team t) |> Map.ofList

    {
        rulesets = plan.rulesets |> List.map ruleset
        teams = teams
        teamMembers =
            let admins =
                plan.orgMemberships
                |> List.filter (fun m -> m.role = OrgAdmin)
                |> List.map _.person

            plan.teams
            |> List.collect (fun t -> t.members |> List.map (teamMember teams admins t))
        teamRepos = plan.teams |> List.collect (fun t -> t.repos |> List.map (teamRepo teams t))
        orgMemberships = plan.orgMemberships |> List.map orgMembership
        environments = plan.environments |> List.map (environment teams)
        orgDefaults = orgDefaults orgSettingsScript org plan.orgDefaults
    }

/// Reads a repo's committed CODEOWNERS. The platform repo is read from the checkout, so a PR that
/// changes leads must change CODEOWNERS in the same PR; other repos are read from their `main`.
let readCodeowners (platformRoot: string) (repo: RepoName) : Task<string option> =
    if repo = RepoName.platform then
        let path = Path.Combine(platformRoot, ".github", "CODEOWNERS")

        Task.FromResult(
            if File.Exists path then
                Some(File.ReadAllText path)
            else
                None
        )
    else
        task {
            try
                let! file =
                    GetRepositoryFile.InvokeAsync(
                        GetRepositoryFileArgs(
                            Repository = RepoName.value repo,
                            File = ".github/CODEOWNERS",
                            Branch = "main"
                        )
                    )

                return Some file.Content
            with _ ->
                return None
        }
