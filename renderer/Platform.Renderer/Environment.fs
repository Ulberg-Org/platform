/// An environment file (`environments/<name>.yaml`, D62): only what the platform decides per environment.
/// Everything else comes from the app's platform.yaml, so environments can't drift on what the app needs.
module Platform.Renderer.Environment

open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open Platform
open Platform.Renderer.App

type DatabaseDto =
    {
        host: string
        port: int
        range: string option
    }

type GatewayDto = { name: string; ``namespace``: string }

type EnvironmentDto =
    {
        name: string
        mode: string
        kubernetesVersion: string
        registry: string option
        git: string
        components: string list
        networkFlows: string
        database: DatabaseDto
        telemetryEndpoint: string
        gateway: GatewayDto
    }

/// How the app's containers run. Develop: the app's Dockerfile target `dev`, built locally, with the source
/// mounted for hot reload (D61). Release: the signed image from the registry, read-only (D54–D56).
type Mode =
    | Develop
    | Release of registry: string

/// How a service's network rule allows its database (D70). By name needs Cilium's DNS inspection, which AKS
/// only has with a paid add-on; Azure allows the database subnet's address range instead.
type DatabaseAccess =
    | ByName
    | ByRange of cidr: string

type Environment =
    {
        name: string
        mode: Mode
        kubernetesVersion: string
        /// Where Argo CD reads the repos: `<git>/<repo>` (D68).
        git: string
        /// The platform components Argo CD runs here (`components/<name>`), besides the apps.
        components: Name list
        /// Whether the network's decisions (allowed and dropped flows) are recorded (D71): Hubble locally, AKS's
        /// Advanced Container Networking Services in Azure (decided in Phase 3).
        networkFlows: bool
        /// The database's host, with {app} and {database} placeholders (see databaseHost in Render.fs).
        databaseHost: string
        databasePort: int
        databaseAccess: DatabaseAccess
        telemetryEndpoint: string
        gatewayName: string
        gatewayNamespace: string
    }

module private Field =
    let private cidr = Regex @"^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$"

    /// Release mode needs the registry its images come from; develop builds locally and takes none.
    let mode (mode: string) (registry: string option) =
        match mode, registry with
        | "develop", None -> Ok Develop
        | "release", Some registry -> Ok(Release registry)
        | "develop", Some _ -> Error "'registry': develop mode builds locally and takes no registry"
        | "release", None -> Error "'registry' is required in release mode"
        | other, _ -> Error $"'mode': '{other}' is not develop or release"

    let databaseAccess (range: string option) =
        match range with
        | None -> Ok ByName
        | Some r -> Parse.matches cidr "database.range" r |> Result.map ByRange

    let components (names: string list) =
        names
        |> List.traverseResultA (Name.parse "components")
        |> Result.mapError (String.concat "; ")

let parse (yaml: string) : Validation<Environment, string> =
    Parse.yaml<EnvironmentDto> yaml
    |> Validation.bind (fun d ->
        validation {
            let! name = Parse.notEmpty "name" d.name
            and! mode = Field.mode d.mode d.registry
            and! kubernetesVersion = Parse.notEmpty "kubernetesVersion" d.kubernetesVersion
            and! git = Parse.notEmpty "git" d.git
            and! components = Field.components d.components
            and! networkFlows = Parse.flag "networkFlows" d.networkFlows
            and! databaseHost = Parse.notEmpty "database.host" d.database.host
            and! databaseAccess = Field.databaseAccess d.database.range
            and! telemetryEndpoint = Parse.notEmpty "telemetryEndpoint" d.telemetryEndpoint

            return
                {
                    name = name
                    mode = mode
                    kubernetesVersion = kubernetesVersion
                    git = git
                    components = components
                    networkFlows = networkFlows
                    databaseHost = databaseHost
                    databasePort = d.database.port
                    databaseAccess = databaseAccess
                    telemetryEndpoint = telemetryEndpoint
                    gatewayName = d.gateway.name
                    gatewayNamespace = d.gateway.``namespace``
                }
        })
