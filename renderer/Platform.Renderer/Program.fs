/// The renderer's commands. Each one: input (files, environment variables) → parse and render (pure) → write
/// the result to stdout, or report every problem on stderr.
///
///   app <platform.yaml> <environment.yaml> <tag>   an app's manifests (what Argo CD deploys for it)
///   plan <platform.yaml> <environment.yaml>        what the platform CLI builds and creates for an app, as JSON
///   cluster <platform repo> <environment>          what Argo CD's root application deploys (Cluster.fs)
///   chart <platform repo> <component>              "<repo> <chart or -> <version> <namespace>", for bootstrapping
///   argocd                                         as Argo CD's `platform` plugin: app or cluster, from ARGOCD_ENV_*
module Platform.Renderer.Program

open System
open System.IO
open System.Text.Json
open FsToolkit.ErrorHandling
open Platform.Renderer

let private within (file: string) (v: Validation<'a, string>) =
    v |> Validation.mapError (fun e -> $"{file}: {e}")

// 1. Input: the files a command reads.

let private readComponent (repo: string) (environment: string) (name: string) =
    let folder = Path.Combine(repo, "components", name)

    let valueFiles =
        [
            $"components/{name}/values.yaml"
            $"components/{name}/values-{environment}.yaml"
        ]
        |> List.filter (fun f -> File.Exists(Path.Combine(repo, f)))

    let manifestFolders =
        [ "common"; environment ]
        |> List.filter (fun f -> Directory.Exists(Path.Combine(folder, f)))

    let file = Path.Combine(folder, "component.yaml")

    if File.Exists file then
        Cluster.parseComponent name valueFiles manifestFolders (File.ReadAllText file)
    else
        Validation.error $"components/{name}: no component.yaml"

let private readApps (repo: string) =
    Directory.GetFiles(Path.Combine(repo, "apps"), "*.yaml")
    |> List.ofArray
    |> List.sort
    |> List.traverseValidationA (fun f -> Cluster.parseApp (Path.GetFileNameWithoutExtension f) (File.ReadAllText f))

// 2. The commands: each one's output text, or every problem. Parsing and rendering are pure (App, Environment,
// Render, Cluster); the commands only feed them what they read.

let private parsed (appYaml: string) (environmentYaml: string) =
    validation {
        let! app = App.parse appYaml |> within "platform.yaml"
        and! environment = Environment.parse environmentYaml |> within "environment"
        return app, environment
    }

let private app appYaml environmentYaml (tag: string) =
    parsed appYaml environmentYaml
    |> Validation.map (fun (app, environment) -> Render.render app environment tag |> Manifest.toStream)

let private plan appYaml environmentYaml =
    parsed appYaml environmentYaml
    |> Validation.map (fun (app, environment) -> Render.plan app environment "dev" |> JsonSerializer.Serialize)

let private cluster (repo: string) (environmentName: string) =
    let environmentYaml =
        File.ReadAllText(Path.Combine(repo, "environments", $"{environmentName}.yaml"))

    validation {
        let! environment =
            Environment.parse environmentYaml
            |> within $"environments/{environmentName}.yaml"

        let! components =
            environment.components
            |> List.traverseValidationA (App.Name.value >> readComponent repo environment.name)

        and! apps = readApps repo
        let! manifests = Cluster.render environment environmentYaml components apps
        return Manifest.toStream manifests
    }

let private chart (repo: string) (name: string) =
    let line (c: Cluster.Component) (ch: Cluster.Chart) =
        let chartName = ch.name |> Option.defaultValue "-"
        $"{ch.repo} {chartName} {ch.version} {App.Name.value c.ns}"

    readComponent repo "none" name
    |> Validation.bind (fun c ->
        c.chart
        |> Result.requireSome $"components/{name}: no chart"
        |> Result.map (line c)
        |> Validation.ofResult)

/// Argo CD runs plugins in the source's folder, with the Application's plugin variables prefixed ARGOCD_ENV_.
let private argocd () =
    let variable name =
        Environment.GetEnvironmentVariable $"ARGOCD_ENV_{name}" |> Option.ofObj // null when unset

    match variable "MODE", variable "ENVIRONMENT", variable "TAG" with
    | Some "app", Some environmentYaml, Some tag -> app (File.ReadAllText "platform.yaml") environmentYaml tag
    | Some "cluster", Some environmentName, _ -> cluster "." environmentName
    | _ -> Validation.error "ARGOCD_ENV_MODE must be app (with ENVIRONMENT and TAG) or cluster (with ENVIRONMENT)"

// 3. Action and 4. Report.

[<EntryPoint>]
let main argv =
    let result =
        match List.ofArray argv with
        | [ "app"; appFile; environmentFile; tag ] ->
            app (File.ReadAllText appFile) (File.ReadAllText environmentFile) tag
        | [ "plan"; appFile; environmentFile ] -> plan (File.ReadAllText appFile) (File.ReadAllText environmentFile)
        | [ "cluster"; repo; environment ] -> cluster repo environment
        | [ "chart"; repo; name ] -> chart repo name
        | [ "argocd" ] -> argocd ()
        | _ -> Validation.error "usage: see the top of Program.fs"

    match result with
    | Ok output ->
        printf "%s" output
        0
    | Error problems ->
        problems |> List.iter (eprintfn "%s")
        1
