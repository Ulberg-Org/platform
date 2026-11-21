/// What the shared stack should hold (D53, D54): everything that outlives environments. Pure data and pure
/// functions, so every rule here is unit-tested; Program.fs only turns it into resources.
module Platform.Shared.Desired

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
