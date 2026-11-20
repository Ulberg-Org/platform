/// An environment file (`environments/<name>.yaml`, D62): only what the platform decides per environment.
/// Everything else comes from the app's platform.yaml, so environments can't drift on what the app needs.
module Platform.Renderer.Environment

open FsToolkit.ErrorHandling
open Platform

type DatabaseDto = { host: string; port: int }
type GatewayDto = { name: string; ``namespace``: string }

type EnvironmentDto =
    {
        name: string
        mode: string
        kubernetesVersion: string
        registry: string option
        database: DatabaseDto
        telemetryEndpoint: string
        gateway: GatewayDto
    }

/// How the app's containers run. Develop: the app's Dockerfile target `dev`, built locally, with the source
/// mounted for hot reload (D61). Release: the signed image from the registry, read-only (D54–D56).
type Mode =
    | Develop
    | Release of registry: string

type Environment =
    {
        name: string
        mode: Mode
        kubernetesVersion: string
        /// The database's host, with {app} and {database} placeholders (see databaseHost in Render.fs).
        databaseHost: string
        databasePort: int
        telemetryEndpoint: string
        gatewayName: string
        gatewayNamespace: string
    }

module private Field =
    /// Release mode needs the registry its images come from; develop builds locally and takes none.
    let mode (mode: string) (registry: string option) =
        match mode, registry with
        | "develop", None -> Ok Develop
        | "release", Some registry -> Ok(Release registry)
        | "develop", Some _ -> Error "'registry': develop mode builds locally and takes no registry"
        | "release", None -> Error "'registry' is required in release mode"
        | other, _ -> Error $"'mode': '{other}' is not develop or release"

let parse (yaml: string) : Validation<Environment, string> =
    Parse.yaml<EnvironmentDto> yaml
    |> Validation.bind (fun d ->
        validation {
            let! name = Parse.notEmpty "name" d.name

            and! mode = Field.mode d.mode d.registry
            and! kubernetesVersion = Parse.notEmpty "kubernetesVersion" d.kubernetesVersion
            and! databaseHost = Parse.notEmpty "database.host" d.database.host
            and! telemetryEndpoint = Parse.notEmpty "telemetryEndpoint" d.telemetryEndpoint

            return
                {
                    name = name
                    mode = mode
                    kubernetesVersion = kubernetesVersion
                    databaseHost = databaseHost
                    databasePort = d.database.port
                    telemetryEndpoint = telemetryEndpoint
                    gatewayName = d.gateway.name
                    gatewayNamespace = d.gateway.``namespace``
                }
        })
