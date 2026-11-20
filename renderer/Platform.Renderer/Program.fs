/// `render <platform.yaml> <environment.yaml> <tag> <output folder>`: input → parse → render → write → report.
module Platform.Renderer.Program

open System.IO
open FsToolkit.ErrorHandling
open Platform.Renderer

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | [ appFile; environmentFile; tag; output ] ->
        // 1. Input
        let appYaml = File.ReadAllText appFile
        let environmentYaml = File.ReadAllText environmentFile

        // 2. Domain (pure)
        let rendered =
            validation {
                let! app = App.parse appYaml |> Validation.mapError (fun e -> $"{appFile}: {e}")

                and! environment =
                    Environment.parse environmentYaml
                    |> Validation.mapError (fun e -> $"{environmentFile}: {e}")

                return Render.render app environment tag, Render.plan app environment tag
            }

        match rendered with
        | Error problems ->
            // 4. Report
            problems |> List.iter (eprintfn "%s")
            1
        | Ok(manifests, plan) ->
            // 3. Action
            let manifestFolder = Path.Combine(output, "manifests")
            Directory.CreateDirectory manifestFolder |> ignore

            for m in manifests do
                File.WriteAllText(Path.Combine(manifestFolder, m.file), Manifest.toYaml m.content)

            // Next to the manifests folder, so applying that folder never picks it up.
            File.WriteAllText(Path.Combine(output, "plan.json"), System.Text.Json.JsonSerializer.Serialize plan)

            // 4. Report
            printfn "Rendered %d manifests into %s" manifests.Length output
            0
    | _ ->
        eprintfn "usage: render <platform.yaml> <environment.yaml> <image tag> <output folder>"
        2
