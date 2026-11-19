/// An app's `platform.yaml` (D47): what the app needs, never how an environment provides it. Parsed from the
/// DTO into domain values that can only hold valid declarations; every problem is reported at once.
module Platform.Renderer.App

open System.Text.RegularExpressions
open FsToolkit.ErrorHandling
open Platform

type ReplicasDto = { min: int; max: int }
type ResourcesDto = { cpu: string; memory: string }
type HealthDto = { live: string; ready: string }

type ServiceDto =
    {
        name: string
        build: string
        image: string
        port: int
        replicas: ReplicasDto
        resources: ResourcesDto
        health: HealthDto
        routes: string list option
        database: string option
    }

type DatabaseDto = { engine: string }

type AppDto =
    {
        apiVersion: string
        app: string
        services: ServiceDto list
        databases: Map<string, DatabaseDto> option
    }

/// A Kubernetes-safe name (DNS-1123 label): lowercase letters, digits and dashes.
type Name = private Name of string

module Name =
    let private pattern = Regex "^[a-z]([a-z0-9-]{0,40}[a-z0-9])?$"

    let parse field (s: string) =
        Parse.matches pattern field s |> Result.map Name

    let value (Name n) = n

type Service =
    {
        name: Name
        /// The build context, relative to the app repo (the Dockerfile lives there).
        build: string
        /// The repository in the registry, e.g. "todo/backend".
        image: string
        port: int
        minReplicas: int
        maxReplicas: int
        cpu: string
        memory: string
        live: string
        ready: string
        routes: string list
        database: Name option
    }

type App =
    {
        name: Name
        services: Service list
        databases: Name list
    }

/// D57: never fewer than two instances, so nothing can rely on being the only one.
let minimumReplicas = 2

/// One parser per field of a platform.yaml, so `service` and `parse` below read as a list of fields.
module private Field =
    let port = Parse.between "port" 1 65535

    let minReplicas (r: ReplicasDto) =
        Parse.atLeast "replicas.min" minimumReplicas r.min
        |> Result.mapError (fun e -> $"{e} (D57)")

    let maxReplicas (r: ReplicasDto) =
        Parse.atLeast "replicas.max" r.min r.max

    let path field = Parse.startsWith field "/"

    let routes (routes: string list option) =
        routes
        |> Option.defaultValue []
        |> List.traverseResultA (path "routes")
        |> Result.mapError (String.concat "; ")

    /// A service may only use a database the app declares.
    let database (declared: Name list) (database: string option) =
        match database with
        | None -> Ok None
        | Some db ->
            declared
            |> List.tryFind (Name.value >> (=) db)
            |> Result.requireSome $"'database': '{db}' is not declared under databases"
            |> Result.map Some

    let engine (key: string, db: DatabaseDto) =
        Parse.equals $"databases.{key}.engine" "postgres" db.engine
        |> Result.bind (fun () -> Name.parse "databases" key)

let private service (databases: Name list) (d: ServiceDto) : Validation<Service, string> =
    validation {
        let! name = Name.parse "name" d.name
        and! build = Parse.notEmpty "build" d.build
        and! image = Parse.notEmpty "image" d.image
        and! port = Field.port d.port
        and! minReplicas = Field.minReplicas d.replicas
        and! maxReplicas = Field.maxReplicas d.replicas
        and! live = Field.path "health.live" d.health.live
        and! ready = Field.path "health.ready" d.health.ready
        and! routes = Field.routes d.routes
        and! database = Field.database databases d.database

        return
            {
                name = name
                build = build
                image = image
                port = port
                minReplicas = minReplicas
                maxReplicas = maxReplicas
                cpu = d.resources.cpu
                memory = d.resources.memory
                live = live
                ready = ready
                routes = routes
                database = database
            }
    }
    |> Parse.within $"services.{d.name}"

/// Parses a `platform.yaml`.
let parse (yaml: string) : Validation<App, string> =
    Parse.yaml<AppDto> yaml
    |> Validation.bind (fun d ->
        validation {
            let! () = Parse.equals "apiVersion" "platform/v1" d.apiVersion
            and! name = Name.parse "app" d.app

            and! databases =
                d.databases
                |> Option.defaultValue Map.empty
                |> Map.toList
                |> List.traverseValidationA (Field.engine >> Validation.ofResult)

            let! services = d.services |> List.traverseValidationA (service databases)

            return
                {
                    name = name
                    services = services
                    databases = databases
                }
        })
