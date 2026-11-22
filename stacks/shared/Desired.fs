/// What the shared stack should hold (D53, D54): everything that outlives environments. Pure data and pure
/// functions, so every rule here is unit-tested; Program.fs only turns it into resources.
module Platform.Shared.Desired

open FsToolkit.ErrorHandling

/// An app whose CI may push images: only from its `main` branch (the OIDC subject), and only into its own
/// repositories in the registry (`<app>/...`).
type AppRegistryAccess =
    {
        app: string
        /// GitHub's immutable OIDC subject prefix, e.g. "repo:Ulberg-Org@358725408/todo@1450696692".
        repoSubject: string
    }

module AppRegistryAccess =
    /// Pushes come only from the app's `main` branch, after a reviewed merge; PR builds never push.
    let subject (a: AppRegistryAccess) = $"{a.repoSubject}:ref:refs/heads/main"

    let repositoryPrefix (a: AppRegistryAccess) = $"{a.app}/"

    let identityName (a: AppRegistryAccess) = $"id-{a.app}-acr-push"

/// "Container Registry Repository Writer": push, pull and tag metadata (so CI can lock the tags it pushes).
let repositoryWriterRole = "2a1e307c-b015-4ebd-883e-5b7698a07328"

/// An Azure ABAC condition that limits a repository role to `prefix`: the role's actions are allowed only
/// on repositories whose name starts with it. Outside it, the role grants nothing.
let repositoryCondition (prefix: string) =
    let actions =
        [ "content/read"; "content/write"; "metadata/read"; "metadata/write" ]
        |> List.map (fun a -> $"!(ActionMatches{{'Microsoft.ContainerRegistry/registries/repositories/{a}'}})")
        |> String.concat " AND "

    $"(({actions}) OR (@Request[Microsoft.ContainerRegistry/registries/repositories:name] "
    + $"StringStartsWithIgnoreCase '{prefix}'))"

/// An image copied into our registry from a release elsewhere (D100), so AKS pulls only from ACR. Copied by
/// digest: the exact bytes that were signed, which can't change; under our name, keeping the release's tag.
type ImportedImage =
    {
        source: string
        target: string
        digest: string
    }

module ImportedImage =
    let private pinned =
        System.Text.RegularExpressions.Regex
            @"^(?<image>[^@:\s]+(:[0-9]+)?/[^@:\s]+):(?<tag>[A-Za-z0-9._-]+)@(?<digest>sha256:[0-9a-f]{64})$"

    /// `pin`: `<registry>/<name>:<tag>@sha256:<digest>`; `name`: the repository in ours.
    let parse (name: string) (pin: string) : Result<ImportedImage, string> =
        let found = pinned.Match pin
        let group (key: string) = found.Groups[key].Value

        found.Success
        |> Result.requireTrue $"'{pin}' isn't pinned as <registry>/<name>:<tag>@sha256:<digest>"
        |> Result.map (fun _ ->
            let image, tag, digest = group "image", group "tag", group "digest"

            {
                source = $"{image}@{digest}"
                target = $"{name}:{tag}"
                digest = digest
            })

/// The environments that may exist (D45): each keeps a static IP and a DNS label here, so its address never
/// changes although it's rebuilt every time.
module Environment =
    let known = [ "dev"; "prod" ]

    let parse (names: string list) : Result<string list, string> =
        match names |> List.filter (fun n -> not (List.contains n known)) with
        | [] -> Ok names
        | unknown ->
            Error $"""shared:environments: unknown {String.concat ", " unknown} (known: {String.concat ", " known})"""

    /// `<prefix>-<environment>`, i.e. <prefix>-dev.<region>.cloudapp.azure.com.
    let dnsLabel (prefix: string) (environment: string) = $"{prefix}-{environment}"

/// Telemetry storage (D89): written by Loki and Tempo from Phase 5; kept 14 days.
module Telemetry =
    let retentionDays = 14
    let containers = [ "loki"; "tempo" ]

/// The monthly budget alert (D89): percentages at which the alert list hears about it.
module Budget =
    let thresholds = [ 50; 80; 100 ]

/// The Azure services the stacks use. A subscription must register each once before using it; `shared` does
/// that as code (idempotent), first, so a stack never fails on a service nobody registered by hand.
/// bootstrap/bootstrap.sh registers only what it needs before any stack exists.
module ResourceProviders =
    let all =
        [
            "Microsoft.ContainerRegistry"
            "Microsoft.ContainerService"
            "Microsoft.DBforPostgreSQL"
            "Microsoft.EventHub"
            "Microsoft.Insights"
            "Microsoft.KeyVault"
            "Microsoft.ManagedIdentity"
            "Microsoft.Network"
            "Microsoft.Security"
            "Microsoft.Storage"
        ]
