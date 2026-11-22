/// What access should exist, computed from people.yaml. Pure data and pure functions: no Pulumi, no I/O,
/// so every rule here is unit-tested. Entra.fs, AzureRbac.fs and GitHub.fs only turn a Plan into
/// resources (functional core, imperative shell).
module Platform.Access.Desired

open Platform

open FsToolkit.ErrorHandling
open Platform.Access.People

// ---------------------------------------------------------------------------------------------
// Entra groups

type GroupName =
    | InfraDevelopers
    | AppDevelopers
    /// Standing read access.
    | PlatformRead
    /// Break-glass write. Only reachable by activating PIM.
    | PlatformWrite
    /// Admin. Only reachable by activating PIM, platform leads only.
    | PlatformAdmin
    /// Claude's agent accounts (D41): read roles only, never PIM-eligible.
    | PlatformAgents

module GroupName =
    let all =
        [
            InfraDevelopers
            AppDevelopers
            PlatformRead
            PlatformWrite
            PlatformAdmin
            PlatformAgents
        ]

    let value =
        function
        | InfraDevelopers -> "infra-developers"
        | AppDevelopers -> "app-developers"
        | PlatformRead -> "platform-read"
        | PlatformWrite -> "platform-write"
        | PlatformAdmin -> "platform-admin"
        | PlatformAgents -> "platform-agents"

    let description =
        function
        | InfraDevelopers -> "Infra developers (access/people.yaml). Standing read only."
        | AppDevelopers -> "App developers (access/people.yaml). App logs, metrics and traces."
        | PlatformRead -> "Read on Azure, AKS and the DB. Contains platform-write."
        | PlatformWrite -> "Break-glass write. PIM only (max 2 h). Contains platform-admin."
        | PlatformAdmin -> "Platform admin. PIM only (max 1 h), platform leads only."
        | PlatformAgents -> "Claude's agent accounts (access/people.yaml agents). Read only, never PIM-eligible."

/// Claude's own Entra account for one developer (D41). It reads what that developer can read day to day,
/// never holds or activates anything privileged, and its sign-ins show up separately in the audit logs.
type Agent = { owner: Person }

module Agent =
    /// e.g. "endre-agent"; also the account's user name.
    let key (a: Agent) = $"{Person.key a.owner}-agent"

    /// e.g. "Claude agent of endre (Ulberg)", so every log line names the person it acts for.
    let displayName (a: Agent) =
        $"Claude agent of {Person.key a.owner} ({GitHubHandle.value a.owner.github})"

[<RequireQualifiedAccess>]
type Member =
    | Person of Person
    | Agent of Agent
    | Group of GroupName

type Membership = { group: GroupName; includes: Member }

module Membership =
    /// Stable resource name, e.g. "infra-developers-endre" or "platform-write-in-platform-read".
    let resourceName m =
        match m.includes with
        | Member.Person p -> $"{GroupName.value m.group}-{Person.key p}"
        | Member.Agent a -> $"{GroupName.value m.group}-{Agent.key a}"
        | Member.Group inner -> $"{GroupName.value inner}-in-{GroupName.value m.group}"

/// What PIM asks for at activation, besides a justification.
type ActivationCheck =
    /// PIM's own MFA check. It accepts the MFA from an earlier sign-in, however old.
    | Mfa
    /// A Conditional Access authentication context: a fresh phishing-resistant sign-in every time (CA05, D39).
    | AuthenticationContext of id: string

/// The authentication context that privileged PIM activations require once Conditional Access is on.
let privilegedActivationContext = "c1"

/// PIM for Groups: membership of `group` is eligible only, activated with `activation` + justification.
type PimRule =
    {
        group: GroupName
        maxHours: int
        activation: ActivationCheck
        eligible: Person list
    }

// ---------------------------------------------------------------------------------------------
// Entra directory roles

/// Built-in Entra roles we assign. Nobody holds Global Admin standing (D35); only the emergency accounts
/// keep it, and those are set up by hand, not here.
type DirectoryRole =
    | GlobalAdministrator
    /// Issues Temporary Access Passes and resets sign-in methods for non-admin users, such as the agent
    /// accounts (onboarding, a lost passkey). Never needs Global Admin for that.
    | AuthenticationAdministrator
    /// Read-only: sign-in and audit logs, Conditional Access and security settings.
    | SecurityReader

module DirectoryRole =
    let value =
        function
        | GlobalAdministrator -> "global-administrator"
        | AuthenticationAdministrator -> "authentication-administrator"
        | SecurityReader -> "security-reader"

    /// Built-in role template ID, the same in every tenant.
    let templateId =
        function
        | GlobalAdministrator -> "62e90394-69f5-4237-9190-012177145e10"
        | AuthenticationAdministrator -> "c4e39bd9-1100-46d3-8c65-fb160da0071f"
        | SecurityReader -> "5d6b6bb7-de71-4623-b4af-96380a352509"

/// A standing, read-only directory role for an agent account.
type AgentDirectoryRole = { agent: Agent; role: DirectoryRole }

// ---------------------------------------------------------------------------------------------
// Licences (group-based: members get them while they are members)

type Licence =
    /// PIM, Conditional Access and Identity Protection.
    | EntraIdP2

module Licence =
    let value =
        function
        | EntraIdP2 -> "entra-id-p2"

    /// The licence's SKU ID, the same in every tenant.
    let skuId =
        function
        | EntraIdP2 -> "84a661c4-e949-4bd2-a560-ed7766fcaf2b"

type GroupLicence = { group: GroupName; licence: Licence }

// ---------------------------------------------------------------------------------------------
// Tenant defaults

/// What every member of the tenant (human or agent) may do on their own, without a role. Entra's defaults
/// allow a lot; here nothing that creates is allowed: groups, apps and guests come from code (this stack),
/// and apps get consent from an admin, not from users.
type TenantDefaults =
    {
        usersCanCreateSecurityGroups: bool
        usersCanCreateM365Groups: bool
        usersCanCreateApps: bool
        usersCanCreateTenants: bool
        usersCanConsentToApps: bool
        usersCanInviteGuests: bool
        usersCanJoinByEmailDomain: bool
        usersCanSignUpForTrials: bool
    }

module TenantDefaults =
    let locked =
        {
            usersCanCreateSecurityGroups = false
            usersCanCreateM365Groups = false
            usersCanCreateApps = false
            usersCanCreateTenants = false
            usersCanConsentToApps = false
            usersCanInviteGuests = false
            usersCanJoinByEmailDomain = false
            usersCanSignUpForTrials = false
        }

/// PIM for a directory role: eligible only, activated with `activation` + justification for at most `maxHours`.
type DirectoryRolePim =
    {
        role: DirectoryRole
        maxHours: int
        activation: ActivationCheck
        eligible: Person list
    }

// ---------------------------------------------------------------------------------------------
// Azure RBAC

type AzureRole =
    | Reader
    | MonitoringReader
    | LogAnalyticsReader
    | CostManagementReader
    | Owner

module AzureRole =
    let definitionId =
        function
        | Reader -> "acdd72a7-3385-48ef-bd42-f606fba81ae7"
        | MonitoringReader -> "43d0d8ad-25c7-4714-9337-8ba259a9fe05"
        | LogAnalyticsReader -> "73c42c96-874c-492b-b04d-ab87d138a893"
        | CostManagementReader -> "72fafb9e-0641-4937-9268-a91bfd8191a3"
        | Owner -> "8e3af657-a8ff-443c-a75c-2fe8c4bcb635"

    /// What Claude's agent accounts get on the subscription (D41): everything a platform developer reads
    /// day to day, and nothing that writes or reads data (no state, no secrets).
    let agentRoles =
        [ Reader; MonitoringReader; LogAnalyticsReader; CostManagementReader ]

[<RequireQualifiedAccess>]
type Grantee =
    | Group of GroupName
    /// An emergency-access account from people.yaml (D36).
    | EmergencyAccount of EntraObjectId

/// A role on the whole subscription. Write roles on app resource groups come in Phase 3.
type RoleGrant = { grantee: Grantee; role: AzureRole }

// ---------------------------------------------------------------------------------------------
// Conditional Access

type CaState =
    | ReportOnly
    | Enforced

module CaState =
    /// "off" | "report-only" | "on". Off means no policies at all (security defaults still on).
    let parse =
        function
        | "off" -> Ok None
        | "report-only" -> Ok(Some ReportOnly)
        | "on" -> Ok(Some Enforced)
        | other -> Error $"access:conditionalAccess must be off, report-only or on, not '{other}'"

type CaUsers =
    | AllUsers
    | Groups of GroupName list
    /// Guests and other external users, e.g. the owner Microsoft account.
    | Guests
    /// Everyone in the tenant except guests and external users.
    | Members

/// What a policy applies to: cloud apps, or an authentication context that something (PIM) asks for.
type CaTarget =
    | Apps of string list
    | Context of id: string

type CaGrant =
    | RequireMfa
    | Block
    /// An Entra authentication strength policy, e.g. phishing-resistant MFA.
    | RequireStrength of policyId: string

type SignInFrequency =
    | Hours of int
    /// A fresh sign-in for every request that hits the policy.
    | EveryTime

type CaSession =
    {
        signIn: SignInFrequency
        /// false: the browser never offers "stay signed in". Entra only allows that on policies for all apps.
        persistentBrowser: bool
    }

/// A policy needs a grant, a session control or both; "neither" can't be expressed.
type CaControls =
    | Grant of CaGrant
    | Session of CaSession
    | GrantAndSession of CaGrant * CaSession

type CaPolicy =
    {
        name: string
        state: CaState
        clientAppTypes: string list
        target: CaTarget
        users: CaUsers
        /// Emergency-access accounts, excluded from every policy.
        excludedUsers: EntraObjectId list
        signInRisk: string list
        controls: CaControls
    }

/// Windows Azure Service Management API (portal, az CLI, ARM).
let private azureManagementApp = "797f4846-ba00-4fd7-ba43-dac1f8f63013"

/// Built-in authentication strength "Phishing-resistant MFA" (passkeys, FIDO2, Windows Hello).
let private phishingResistant =
    "/policies/authenticationStrengthPolicies/00000000-0000-0000-0000-000000000004"

let private conditionalAccess (state: CaState option) (access: Access) =
    let policy name clientAppTypes target users signInRisk controls state =
        {
            name = name
            state = state
            clientAppTypes = clientAppTypes
            target = target
            users = users
            excludedUsers = access.emergencyAccess
            signInRisk = signInRisk
            controls = controls
        }

    let policies =
        [
            policy "CA01 Require MFA for everyone" [ "all" ] (Apps [ "All" ]) AllUsers [] (Grant RequireMfa)
            policy
                "CA02 Block legacy authentication"
                [ "exchangeActiveSync"; "other" ]
                (Apps [ "All" ])
                AllUsers
                []
                (Grant Block)
            policy
                "CA03 Phishing-resistant MFA for Azure management"
                [ "all" ]
                (Apps [ azureManagementApp ])
                // Not guests: the owner Microsoft account can't register a passkey here, and would lose
                // the portal and billing. Guests have no Azure access beyond billing; CA01 still needs MFA.
                Members
                []
                (Grant(RequireStrength phishingResistant))
            policy
                "CA04 Risky sign-ins by infra developers need MFA"
                [ "all" ]
                (Apps [ "All" ])
                (Groups [ InfraDevelopers; PlatformAgents ])
                [ "medium"; "high" ]
                (Grant RequireMfa)
            // D39: PIM asks for this context, so every activation needs a fresh passkey sign-in.
            policy
                "CA05 Fresh phishing-resistant sign-in for every PIM activation"
                [ "all" ]
                (Context privilegedActivationContext)
                AllUsers
                []
                (GrantAndSession(
                    RequireStrength phishingResistant,
                    {
                        signIn = EveryTime
                        persistentBrowser = true
                    }
                ))
            policy
                "CA06 Infra developers sign in to Azure management every 4 h"
                [ "all" ]
                (Apps [ azureManagementApp ])
                (Groups [ InfraDevelopers; PlatformAgents ])
                []
                (Session
                    {
                        signIn = Hours 4
                        persistentBrowser = true
                    })
            policy
                "CA07 Developers sign in every 12 h"
                [ "all" ]
                (Apps [ "All" ])
                (Groups [ InfraDevelopers; AppDevelopers; PlatformAgents ])
                []
                (Session
                    {
                        signIn = Hours 12
                        persistentBrowser = true
                    })
            // D39: guests (the owner account keeps billing) sign in again every 12 h too.
            policy
                "CA09 Guests sign in every 12 h"
                [ "all" ]
                (Apps [ "All" ])
                Guests
                []
                (Session
                    {
                        signIn = Hours 12
                        persistentBrowser = true
                    })
            // Part of D39 for the Azure tools: Entra only allows "never stay signed in" on all apps.
            policy
                "CA08 Infra developers never stay signed in"
                [ "all" ]
                (Apps [ "All" ])
                (Groups [ InfraDevelopers; PlatformAgents ])
                []
                (Session
                    {
                        signIn = Hours 12
                        persistentBrowser = false
                    })
        ]

    match state with
    | None -> []
    | Some state -> policies |> List.map (fun p -> p state)

// ---------------------------------------------------------------------------------------------
// GitHub

/// Status checks (job names) a PR must pass, per repo.
let private requiredChecks =
    Map
        [
            "platform",
            [
                "secret scan (gitleaks)"
                "prompt log"
                "dotnet (format, build, test)"
                "pulumi preview"
                // Run from main's copy, so a pull request can't change it (guard.yml).
                "workflow guard"
            ]
            "todo",
            [
                "secret scan (gitleaks)"
                "prompt log"
                "fsharp style"
                "backend (format, build, test)"
                "image (build, scan, SBOM)"
            ]
        ]

type Ruleset =
    {
        repo: RepoName
        requiredChecks: string list
    }

type TeamPermission =
    | Push
    | Maintain

/// A GitHub team in the organization, with its members and what it may do in which repo.
type Team =
    {
        slug: string
        description: string
        members: Person list
        repos: (RepoName * TeamPermission) list
    }

module Team =
    /// The code owners of `repo`: `@<org>/<repo>-leads` in its CODEOWNERS.
    let leadsOf (repo: RepoName) = $"{RepoName.value repo}-leads"

type OrgRole =
    | OrgMember
    | OrgAdmin

/// Membership in the GitHub organization. Only the bootstrap owner is an admin.
type OrgMembership = { person: Person; role: OrgRole }

/// A GitHub environment: runs from `main` only. `branchPolicyId`: the id of the `main` branch policy of one
/// that already existed, which is imported rather than created. `reviewers`: the team that approves its runs.
type Environment =
    {
        repo: RepoName
        name: string
        branchPolicyId: int option
        reviewers: string option
    }

/// What members of the organization may do on their own (D43). Repos, forks and access come from code.
type OrgDefaults =
    {
        baseRepositoryPermission: string
        membersCanCreateRepositories: bool
        membersCanForkPrivateRepositories: bool
    }

module OrgDefaults =
    let locked =
        {
            baseRepositoryPermission = "none"
            membersCanCreateRepositories = false
            membersCanForkPrivateRepositories = false
        }

// ---------------------------------------------------------------------------------------------
// CI identities: who may get an Azure token from GitHub Actions

/// The two managed identities bootstrap.sh created for CI. The stack manages which GitHub OIDC subjects
/// they trust; their roles and Graph permissions stay with the bootstrap.
type CiIdentity =
    | ApplyIdentity
    | PreviewIdentity

module CiIdentity =
    let name =
        function
        | ApplyIdentity -> "id-platform-apply"
        | PreviewIdentity -> "id-platform-preview"

    /// Credential-name suffix, subject claim, and whether bootstrap.sh made it: approved apply runs (the `dev`
    /// environment) and teardowns (the `teardown` environment, D45); previews from pull requests and from main.
    let claims =
        function
        | ApplyIdentity -> [ "", "environment:dev", true; "-teardown", "environment:teardown", false ]
        | PreviewIdentity -> [ "", "pull_request", true; "-main", "ref:refs/heads/main", true ]

/// A repository prefix that CI tokens may carry (e.g. "repo:Ulberg-Org@358725408/platform@1450696781"),
/// and the name its credentials get. `existing`: the credentials already exist (`madeBy: bootstrap` in the
/// config, i.e. bootstrap.sh made them) and are imported, not created.
type CiTrust =
    {
        name: string
        repoPrefix: string
        existing: bool
    }

type CiTrustDto =
    {
        name: string
        repoPrefix: string
        madeBy: string option
    }

module CiTrust =
    let private name =
        System.Text.RegularExpressions.Regex "^[A-Za-z0-9][A-Za-z0-9_-]{2,100}$"

    let private repoPrefix = System.Text.RegularExpressions.Regex "^repo:[^:\\s]+$"

    let private ofDto (i: int) (d: CiTrustDto) : Validation<CiTrust, string> =
        validation {
            let! n = Parse.matches name "name" d.name
            and! p = Parse.matches repoPrefix "repoPrefix" d.repoPrefix

            and! existing =
                match d.madeBy with
                | None -> Ok false
                | Some "bootstrap" -> Ok true
                | Some other -> Error $"'madeBy': '{other}' is not 'bootstrap'"

            return
                ({
                    name = n
                    repoPrefix = p
                    existing = existing
                }
                : CiTrust)
        }
        |> Parse.within $"access:ciTrust[{i}]"

    /// Parses the stack config `access:ciTrust` (a list, as JSON). At least one entry, or CI loses Azure.
    let parse (json: string) : Validation<CiTrust list, string> =
        Parse.yaml<CiTrustDto list> json
        |> Validation.bind (List.mapi ofDto >> List.sequenceValidationA)
        |> Validation.bind (fun ts ->
            if List.isEmpty ts then
                Validation.error "access:ciTrust is empty: CI could never get an Azure token"
            else
                Validation.ok ts)

type FederatedCredential =
    {
        identity: CiIdentity
        name: string
        subject: string
        existing: bool
    }

/// Every trusted repository prefix × every subject each identity accepts.
let federatedCredentials (trusts: CiTrust list) : FederatedCredential list =
    [
        for t in trusts do
            for identity in [ ApplyIdentity; PreviewIdentity ] do
                for suffix, claim, byBootstrap in CiIdentity.claims identity ->
                    {
                        identity = identity
                        name = t.name + suffix
                        subject = $"{t.repoPrefix}:{claim}"
                        // Imported only if bootstrap.sh made both the trust and this claim.
                        existing = t.existing && byBootstrap
                    }
    ]

// ---------------------------------------------------------------------------------------------
// Environments: their resource groups and who reaches their clusters (D84, D88, D95)

/// An environment, as a stack in stacks/environment. Its resource group lives here, not there, so the access
/// granted on it outlives the environment's start and destroy (D95).
type AzureEnvironment = { name: string; location: string }

module AzureEnvironment =
    let private location =
        System.Text.RegularExpressions.Regex @"(?m)^\s*azure-native:location:\s*(\S+)\s*$"

    /// From the stack's config file: its name (`Pulumi.<name>.yaml`) and its `azure-native:location`.
    let parse (fileName: string) (yaml: string) : Result<AzureEnvironment, string> =
        let found = location.Match yaml

        if found.Success then
            Ok
                {
                    name = fileName.Substring("Pulumi.".Length, fileName.Length - "Pulumi..yaml".Length)
                    location = found.Groups[1].Value
                }
        else
            Error $"stacks/environment/{fileName}: azure-native:location is missing"

    let group (environment: AzureEnvironment) = $"rg-{environment.name}"

/// The cluster's roles (D84, D88).
type ClusterRole =
    /// Lets them fetch the kubeconfig at all; what they may do is decided by the RBAC roles.
    | ClusterUser
    | RbacReader
    | RbacClusterAdmin

module ClusterRole =
    let definitionId =
        function
        | ClusterUser -> "4abbcc35-e782-43d8-92c5-2d3f1bd2253f"
        | RbacReader -> "7f6c6a51-bcf8-42ba-9220-52d62157d7db"
        | RbacClusterAdmin -> "b1ff04bb-8a4e-4dc4-8eb5-8693973ce19b"

[<RequireQualifiedAccess>]
type ClusterGrantee =
    | Group of GroupName
    | Ci of CiIdentity

module Cluster =
    /// Granted on each environment's resource group, so every cluster in it inherits them. Readers, every
    /// Claude agent and CI's preview read (no Secrets); only platform-write (through PIM) and CI's apply (the
    /// bootstrap: the database's roles, Argo CD in Phase 4) change anything. App developers have nothing in the
    /// cluster (AGENTS.md §3).
    let access =
        [
            ClusterGrantee.Group PlatformRead, [ ClusterUser; RbacReader ]
            ClusterGrantee.Group PlatformAgents, [ ClusterUser; RbacReader ]
            ClusterGrantee.Group PlatformWrite, [ ClusterUser; RbacClusterAdmin ]
            ClusterGrantee.Ci ApplyIdentity, [ ClusterUser; RbacClusterAdmin ]
            ClusterGrantee.Ci PreviewIdentity, [ ClusterUser; RbacReader ]
        ]

// ---------------------------------------------------------------------------------------------
// Claude's GitHub identity (D52)

/// The Claude GitHub App's private key: a Key Vault key that may only sign. `signers` may sign with it, to
/// mint 1 h installation tokens; nobody can read it, so the app's identity can't leave Key Vault.
type ClaudeAppKey = { keyName: string; signers: GroupName }

let claudeAppKey =
    {
        keyName = "claude-github-app"
        signers = PlatformAgents
    }

// ---------------------------------------------------------------------------------------------
// The plan

type Plan =
    {
        groups: GroupName list
        memberships: Membership list
        pim: PimRule list
        directoryRoles: DirectoryRolePim list
        roleAssignments: RoleGrant list
        conditionalAccess: CaPolicy list
        rulesets: Ruleset list
        teams: Team list
        orgMemberships: OrgMembership list
        orgDefaults: OrgDefaults
        environments: Environment list
        /// Who gets PIM emails: every activation and every new assignment of a privileged role (D40).
        alertEmails: Email list
        /// Claude's agent accounts (D41) and the tenant domain their user names live in.
        agents: Agent list
        agentDirectoryRoles: AgentDirectoryRole list
        tenantDomain: string
        licences: GroupLicence list
        tenantDefaults: TenantDefaults
        /// Which GitHub OIDC subjects the CI identities trust.
        ciCredentials: FederatedCredential list
    }

let private memberships (access: Access) =
    let people group (persons: Person list) =
        persons
        |> List.map (fun p ->
            {
                group = group
                includes = Member.Person p
            })

    let agents =
        access.agents
        |> List.map (fun p ->
            {
                group = PlatformAgents
                includes = Member.Agent { owner = p }
            })

    people InfraDevelopers access.infraDevelopers
    @ people AppDevelopers access.appDevelopers
    @ agents
    // read ⊃ write ⊃ admin, so each consumer only maps three levels.
    @ [
        {
            group = PlatformRead
            includes = Member.Group InfraDevelopers
        }
        {
            group = PlatformRead
            includes = Member.Group PlatformWrite
        }
        {
            group = PlatformWrite
            includes = Member.Group PlatformAdmin
        }
    ]

let private rulesets (access: Access) =
    access.repoLeads
    |> Map.keys
    |> List.ofSeq
    |> List.traverseResultA (fun repo ->
        requiredChecks
        |> Map.tryFind (RepoName.value repo)
        |> Result.requireSome $"no required checks defined for repo '{RepoName.value repo}'"
        |> Result.map (fun checks -> { repo = repo; requiredChecks = checks }))

/// Leads maintain their repo; infra developers push to every repo, app developers to every app repo.
let private teams (access: Access) =
    let repos = access.repoLeads |> Map.keys |> List.ofSeq
    let appRepos = repos |> List.filter ((<>) RepoName.platform)

    let leads =
        [
            for KeyValue(repo, leads) in access.repoLeads ->
                {
                    slug = Team.leadsOf repo
                    description = $"Code owners of {RepoName.value repo}: approve and merge (people.yaml repo-leads)"
                    members = leads
                    repos = [ repo, Maintain ]
                }
        ]

    leads
    @ [
        {
            slug = "infra-developers"
            description = "Infra developers (people.yaml groups)"
            members = access.infraDevelopers
            repos = repos |> List.map (fun r -> r, Push)
        }
        {
            slug = "app-developers"
            description = "App developers (people.yaml groups)"
            members = access.appDevelopers
            repos = appRepos |> List.map (fun r -> r, Push)
        }
    ]

/// Everyone in people.yaml is a member of the organization; only the bootstrap owner is an admin.
let private orgMemberships (orgAdmin: GitHubHandle) (access: Access) =
    [
        for person in access.people ->
            {
                person = person
                role =
                    if GitHubHandle.same person.github orgAdmin then
                        OrgAdmin
                    else
                        OrgMember
            }
    ]

let plan
    (orgAdmin: GitHubHandle)
    (caState: CaState option)
    (alertEmails: Email list)
    (tenantDomain: string)
    (ciTrusts: CiTrust list)
    (access: Access)
    : Result<Plan, string list> =

    // PIM requires the authentication context only once CA05 is enforced. In report-only, CA05 checks
    // nothing, so PIM keeps its own MFA check.
    let activation =
        match caState with
        | Some Enforced -> AuthenticationContext privilegedActivationContext
        | Some ReportOnly
        | None -> Mfa

    result {
        let! rulesets = rulesets access
        let! todo = RepoName.parse "todo" |> Result.mapError List.singleton

        return
            {
                groups = GroupName.all
                memberships = memberships access
                pim =
                    [
                        {
                            group = PlatformWrite
                            maxHours = 2
                            activation = activation
                            eligible = access.infraDevelopers
                        }
                        {
                            group = PlatformAdmin
                            maxHours = 1
                            activation = activation
                            eligible = access.platformLeads
                        }
                    ]
                // Signing in an agent (onboarding, a lost passkey) needs a Temporary Access Pass; Authentication
                // Administrator issues it, so that never needs Global Admin. Same rules for both.
                directoryRoles =
                    [
                        for role in [ GlobalAdministrator; AuthenticationAdministrator ] ->
                            {
                                role = role
                                maxHours = 1
                                activation = activation
                                eligible = access.platformLeads
                            }
                    ]
                // Subscription Owner only through the emergency accounts (D36); nobody else holds it standing.
                roleAssignments =
                    {
                        grantee = Grantee.Group PlatformRead
                        role = Reader
                    }
                    :: [
                        for role in AzureRole.agentRoles ->
                            {
                                grantee = Grantee.Group PlatformAgents
                                role = role
                            }
                    ]
                    @ [
                        for id in access.emergencyAccess ->
                            {
                                grantee = Grantee.EmergencyAccount id
                                role = Owner
                            }
                    ]
                conditionalAccess = conditionalAccess caState access
                rulesets = rulesets
                teams = teams access
                orgMemberships = orgMemberships orgAdmin access
                orgDefaults = OrgDefaults.locked
                // Applies need a platform lead's approval; app releases (the pr-bot) run from main only.
                environments =
                    [
                        {
                            repo = RepoName.platform
                            name = "dev"
                            branchPolicyId = Some 65981291
                            reviewers = Some(Team.leadsOf RepoName.platform)
                        }
                        // Tearing environments down needs no approval (D45): only `destroy` runs in it.
                        {
                            repo = RepoName.platform
                            name = "teardown"
                            branchPolicyId = None
                            reviewers = None
                        }
                        {
                            repo = todo
                            name = "release"
                            branchPolicyId = Some 65981293
                            reviewers = None
                        }
                    ]
                alertEmails = alertEmails
                agents = access.agents |> List.map (fun p -> { owner = p })
                agentDirectoryRoles =
                    [
                        for p in access.agents ->
                            {
                                agent = { owner = p }
                                role = SecurityReader
                            }
                    ]
                tenantDomain = tenantDomain
                // Infra developers need P2 for PIM; agents get the same Conditional Access as humans (D41).
                tenantDefaults = TenantDefaults.locked
                licences =
                    [
                        {
                            group = InfraDevelopers
                            licence = EntraIdP2
                        }
                        {
                            group = PlatformAgents
                            licence = EntraIdP2
                        }
                    ]
                ciCredentials = federatedCredentials ciTrusts
            }
    }
