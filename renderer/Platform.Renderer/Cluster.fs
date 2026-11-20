/// What the platform runs in one environment (D67, D68): Argo CD's root application renders this from the
/// platform repo. Per app: its project (what it may deploy, and where), its namespace with the deny-all rule,
/// and the Application that renders its platform.yaml. Per platform component: an Application for its Helm
/// chart and manifests.
module Platform.Renderer.Cluster

open FsToolkit.ErrorHandling
open Platform
open Platform.Renderer.App
open Platform.Renderer.Environment
open Platform.Renderer.Manifest

/// A Helm chart: from a classic repository (`name` set) or an OCI registry (`repo` is the chart itself).
type Chart =
    {
        repo: string
        name: string option
        version: string
    }

type ComponentDto =
    {
        ``namespace``: string
        chart: Chart option
    }

type RegisteredAppDto =
    {
        repo: string
        tags: Map<string, string> option
    }

/// A platform component (`components/<name>/component.yaml`): its namespace, its chart if it has one, and
/// which of its files apply in this environment (found by the caller).
type Component =
    {
        name: Name
        ns: Name
        chart: Chart option
        /// Helm values files, relative to the platform repo: `values.yaml`, then `values-<environment>.yaml`.
        valueFiles: string list
        /// Plain manifests, relative to the component: `common/`, then `<environment>/`.
        manifestFolders: string list
    }

/// An app the platform runs (`apps/<name>.yaml`): its repo, and in release mode the image tag per environment.
type RegisteredApp =
    {
        name: Name
        repo: string
        tags: Map<string, string>
    }

module private Field =
    let chart (c: Chart) =
        Parse.notEmpty "chart.version" c.version |> Result.map (fun _ -> c)

let parseComponent (name: string) (valueFiles: string list) (manifestFolders: string list) (yaml: string) =
    Parse.yaml<ComponentDto> yaml
    |> Validation.bind (fun d ->
        validation {
            let! name = Name.parse "name" name
            and! ns = Name.parse "namespace" d.``namespace``
            and! chart = d.chart |> Option.traverseResult Field.chart

            return
                {
                    name = name
                    ns = ns
                    chart = chart
                    valueFiles = valueFiles
                    manifestFolders = manifestFolders
                }
        })
    |> Parse.within $"components/{name}"

let parseApp (name: string) (yaml: string) =
    Parse.yaml<RegisteredAppDto> yaml
    |> Validation.bind (fun d ->
        validation {
            let! name = Name.parse "name" name
            and! repo = Parse.notEmpty "repo" d.repo

            return
                {
                    name = name
                    repo = repo
                    tags = d.tags |> Option.defaultValue Map.empty
                }
        })
    |> Parse.within $"apps/{name}"

/// The image tag an app runs in this environment: locally its dev build; in release mode the tag the
/// environment's GitOps change set (Phase 4).
let tag (environment: Environment) (app: RegisteredApp) : Result<string, string> =
    match environment.mode with
    | Develop -> Ok "dev"
    | Release _ ->
        app.tags
        |> Map.tryFind environment.name
        |> Result.requireSome $"apps/{Name.value app.name}: no image tag for '{environment.name}'"

let private inCluster = "https://kubernetes.default.svc"

/// What must exist before an app's Application (its project, namespace and deny-all rule): Argo CD syncs lower
/// waves first, and the Application is refused while its project is missing.
let private first =
    "annotations", fields [ "argocd.argoproj.io/sync-wave", text "-1" ]

let private argo (kind: string) (name: string) (annotations: (string * Node) list) (spec: Node) =
    resource
        "argoproj.io/v1alpha1"
        kind
        ([ "name", text name; "namespace", text "argocd" ] @ annotations)
        [ "spec", spec ]

/// A git source at the head of `main`, as every Application here reads its repos.
let private fromGit (repo: string) (rest: (string * Node) list) =
    fields ([ "repoURL", text repo; "targetRevision", text "main" ] @ rest)

/// Every Application syncs on its own, removes what's no longer rendered and undoes changes made by hand.
let private syncPolicy (options: string list) =
    fields
        [
            "automated", fields [ "prune", Flag true; "selfHeal", Flag true ]
            "syncOptions", items (options |> List.map text)
            "retry",
            fields
                [
                    "limit", Number 10
                    "backoff", fields [ "duration", text "5s"; "maxDuration", text "1m" ]
                ]
        ]

/// What an app may deploy (deny by default): only these kinds, only into its own namespace, from its own repo.
/// The namespace, its deny-all rule and anything cluster-wide belong to the platform.
let private appKinds =
    [
        "apps", "Deployment"
        "", "Service"
        "gateway.networking.k8s.io", "HTTPRoute"
        "cilium.io", "CiliumNetworkPolicy"
    ]

let private project (environment: Environment) (app: RegisteredApp) =
    let name = Name.value app.name

    let kind (group, kind) =
        fields [ "group", text group; "kind", text kind ]

    let spec =
        fields
            [
                "sourceRepos", items [ text $"{environment.git}/{app.repo}" ]
                "destinations", items [ fields [ "server", text inCluster; "namespace", text name ] ]
                "clusterResourceWhitelist", items []
                "namespaceResourceWhitelist", items (appKinds |> List.map kind)
            ]

    {
        file = $"{name}-project.yaml"
        content = argo "AppProject" name [ first ] spec
    }

let private ns (app: RegisteredApp) =
    {
        file = $"{Name.value app.name}-namespace.yaml"
        content = resource "v1" "Namespace" [ "name", text (Name.value app.name); first ] []
    }

/// Deny by default (AGENTS.md §2): nothing in the app's namespace talks to or from anything unless one of the
/// app's rendered rules allows it.
let private denyAll (app: RegisteredApp) =
    let metadata =
        [ "name", text "deny-all"; "namespace", text (Name.value app.name); first ]

    let spec =
        fields
            [
                "podSelector", fields []
                "policyTypes", items [ text "Ingress"; text "Egress" ]
            ]

    {
        file = $"{Name.value app.name}-deny-all.yaml"
        content = resource "networking.k8s.io/v1" "NetworkPolicy" metadata [ "spec", spec ]
    }

/// The app's Application: Argo CD's `platform` plugin (the renderer) turns its platform.yaml into manifests with
/// this environment's file, passed along whole, so both are read from the same commit of the platform repo.
let private application (environment: Environment) (environmentYaml: string) (app: RegisteredApp) (tag: string) =
    let name = Name.value app.name

    let variable (key: string) (value: string) =
        fields [ "name", text key; "value", text value ]

    let plugin =
        fields
            [
                "name", text "platform"
                "env",
                items
                    [
                        variable "MODE" "app"
                        variable "ENVIRONMENT" environmentYaml
                        variable "TAG" tag
                    ]
            ]

    let source =
        fromGit $"{environment.git}/{app.repo}" [ "path", text "."; "plugin", plugin ]

    let spec =
        fields
            [
                "project", text name
                "source", source
                "destination", fields [ "server", text inCluster; "namespace", text name ]
                "syncPolicy", syncPolicy []
            ]

    {
        file = $"{name}-application.yaml"
        content = argo "Application" name [] spec
    }

/// A platform component's Application: its chart with the platform repo's values files, and its manifests.
let private componentApplication (environment: Environment) (c: Component) =
    let name = Name.value c.name
    let platformRepo = $"{environment.git}/platform"

    let helm =
        fields
            [
                "releaseName", text name
                "valueFiles", items (c.valueFiles |> List.map (fun f -> text $"$values/{f}"))
            ]

    // An OCI chart is the repository itself: the source's whole path.
    let chartIn (chart: Chart) =
        match chart.name with
        | Some n -> "chart", text n
        | None -> "path", text "."

    let chartSource (chart: Chart) =
        fields
            [
                "repoURL", text chart.repo
                "targetRevision", text chart.version
                chartIn chart
                "helm", helm
            ]

    let values = fromGit platformRepo [ "ref", text "values" ]

    let manifestsIn (folder: string) =
        fromGit platformRepo [ "path", text $"components/{name}/{folder}" ]

    let chart = c.chart |> Option.map chartSource |> Option.toList
    let valuesRef = c.chart |> Option.map (fun _ -> values) |> Option.toList
    let manifests = c.manifestFolders |> List.map manifestsIn

    let spec =
        fields
            [
                "project", text "default"
                "sources", items (chart @ valuesRef @ manifests)
                "destination", fields [ "server", text inCluster; "namespace", text (Name.value c.ns) ]
                // Charts bring large CRDs (server-side apply) and resources of kinds they define themselves.
                "syncPolicy",
                syncPolicy
                    [
                        "CreateNamespace=true"
                        "ServerSideApply=true"
                        "SkipDryRunOnMissingResource=true"
                    ]
            ]

    {
        file = $"{name}-component.yaml"
        content = argo "Application" name [] spec
    }

/// Everything Argo CD's root application deploys in `environment`; refused if an app has no tag for it.
let render
    (environment: Environment)
    (environmentYaml: string)
    (components: Component list)
    (apps: RegisteredApp list)
    : Result<Manifest list, string list> =
    let appManifests (app: RegisteredApp) (tag: string) =
        [
            project environment app
            ns app
            denyAll app
            application environment environmentYaml app tag
        ]

    apps
    |> List.traverseResultA (fun app -> tag environment app |> Result.map (appManifests app))
    |> Result.map (fun perApp -> List.map (componentApplication environment) components @ List.concat perApp)
