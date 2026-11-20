module Platform.Renderer.Tests.ClusterTests

open System
open System.IO
open Expecto
open Platform.Renderer

let private ok r =
    r |> Result.defaultWith (failtestf "expected Ok, got %A")

let private environmentYaml name =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "environments", $"{name}.yaml"))

let private argocd =
    Cluster.parseComponent
        "argocd"
        [ "components/argocd/values.yaml"; "components/argocd/values-local.yaml" ]
        [ "common" ]
        "namespace: argocd\nchart: { repo: https://argoproj.github.io/argo-helm, name: argo-cd, version: 10.14.0, digest: sha256:0000000000000000000000000000000000000000000000000000000000000000 }"
    |> ok

let private gateway =
    Cluster.parseComponent
        "gateway"
        []
        [ "common" ]
        "namespace: gateway\nchart: { repo: oci://docker.io/envoyproxy/gateway-helm, version: v1.10.0, digest: sha256:0000000000000000000000000000000000000000000000000000000000000000 }"
    |> ok

let private todo tags =
    Cluster.parseApp "todo" $"repo: todo\ntags: {tags}" |> ok

/// Everything the root application deploys in `env`, as YAML text by file name.
let private rendered env app =
    let yaml = environmentYaml env

    Cluster.render (Environment.parse yaml |> ok) yaml [ argocd; gateway ] [ app ]
    |> Result.map (List.map (fun m -> m.file, Manifest.toYaml m.content) >> Map.ofList)

[<Tests>]
let tests =
    testList
        "cluster"
        [
            test "the local platform runs the telemetry stack (D64), each component present and valid" {
                let repo = AppContext.BaseDirectory
                let environment = environmentYaml "local" |> Environment.parse |> ok
                let names = environment.components |> List.map App.Name.value

                for c in [ "observability"; "collector"; "loki"; "tempo"; "prometheus"; "grafana" ] do
                    Expect.contains names c $"local runs {c}"

                for c in names do
                    let file = Path.Combine(repo, "components", c, "component.yaml")
                    Expect.isTrue (File.Exists file) $"components/{c}/component.yaml"
                    Expect.isOk (Cluster.parseComponent c [] [] (File.ReadAllText file)) $"components/{c} parses"
            }

            test "a chart is pinned by its content (D65): without its sha256 digest it's refused" {
                let withoutDigest =
                    "namespace: x\nchart: { repo: https://example.org, name: x, version: 1.0.0 }"

                Expect.isError (Cluster.parseComponent "x" [] [] withoutDigest) "no digest"

                let badDigest =
                    "namespace: x\nchart: { repo: https://example.org, name: x, version: 1.0.0, digest: md5:abc }"

                Expect.isError (Cluster.parseComponent "x" [] [] badDigest) "not sha256"
            }

            test "an app may only deploy ordinary kinds, into its own namespace, from its own repo" {
                let project = (rendered "local" (todo "{}") |> ok)["todo-project.yaml"]
                Expect.stringContains project "- git://git.gitops.svc.cluster.local/todo" "its repo"
                Expect.stringContains project "namespace: todo" "its namespace"
                Expect.stringContains project "clusterResourceWhitelist: []" "nothing cluster-wide"
                Expect.stringContains project "kind: Deployment" "deployments"
                Expect.isFalse (project.Contains "kind: NetworkPolicy") "not the deny-all rule"
            }

            test "the platform owns the app's namespace and its deny-all rule" {
                let manifests = rendered "local" (todo "{}") |> ok
                Expect.stringContains manifests["todo-namespace.yaml"] "kind: Namespace" "namespace"
                Expect.stringContains manifests["todo-deny-all.yaml"] "podSelector: {}" "every pod"
            }

            test "the app is rendered by Argo CD's plugin with the environment file passed along whole" {
                let application = (rendered "local" (todo "{}") |> ok)["todo-application.yaml"]
                Expect.stringContains application "name: platform" "the plugin"
                Expect.stringContains application "kubernetesVersion" "the environment file"
                Expect.stringContains application "value: dev" "the local tag"
            }

            test "a component is its chart with the platform repo's values, plus its manifests" {
                let argo = (rendered "local" (todo "{}") |> ok)["argocd-component.yaml"]
                Expect.stringContains argo "chart: argo-cd" "the chart"
                Expect.stringContains argo "$values/components/argocd/values-local.yaml" "local values"
                Expect.stringContains argo "path: components/argocd/common" "manifests"
                Expect.stringContains argo "ServerSideApply=true" "large CRDs"
            }

            test "a chart from an OCI registry is the source's whole path" {
                let gw = (rendered "local" (todo "{}") |> ok)["gateway-component.yaml"]
                Expect.stringContains gw "repoURL: oci://docker.io/envoyproxy/gateway-helm" "the chart"
                Expect.stringContains gw "path: ." "path"
            }

            test "an app's project, namespace and deny-all rule exist before its Application" {
                let manifests = rendered "local" (todo "{}") |> ok

                for file in [ "todo-project.yaml"; "todo-namespace.yaml"; "todo-deny-all.yaml" ] do
                    Expect.stringContains manifests[file] "argocd.argoproj.io/sync-wave: \"-1\"" file

                Expect.isFalse (manifests["todo-application.yaml"].Contains "sync-wave") "the Application after"
            }

            test "in release mode an app without a tag for the environment is refused" {
                Expect.isError (rendered "dev" (todo "{}")) "no tag"

                let application =
                    (rendered "dev" (todo "{ dev: 55fb0bd }") |> ok)["todo-application.yaml"]

                Expect.stringContains application "value: 55fb0bd" "the tag"
                Expect.stringContains application "https://github.com/Ulberg-Org/todo" "GitHub"
            }
        ]
