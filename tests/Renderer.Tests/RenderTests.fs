module Platform.Renderer.Tests.RenderTests

open System
open System.IO
open Expecto
open Platform.Renderer

let private app =
    """
apiVersion: platform/v1
app: todo
services:
  - name: backend
    build: backend
    image: todo/backend
    port: 8080
    replicas: { min: 2, max: 4 }
    resources: { cpu: 250m, memory: 256Mi }
    health: { port: 8081, live: /health/live, ready: /health/ready }
    routes: [ /, /api ]
    database: todo
  - name: worker
    build: worker
    image: todo/worker
    port: 8080
    replicas: { min: 2, max: 2 }
    resources: { cpu: 100m, memory: 128Mi }
    health: { port: 9090, live: /live, ready: /ready }
databases:
  todo: { engine: postgres }
"""

let private ok r =
    r |> Result.defaultWith (failtestf "expected Ok, got %A")

let private environment name =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "environments", $"{name}.yaml"))
    |> Environment.parse
    |> ok

/// All manifests for `env`, as YAML text by file name.
let private rendered env =
    Render.render (App.parse app |> ok) (environment env) "55fb0bd"
    |> List.map (fun m -> m.file, Manifest.toYaml m.content)
    |> Map.ofList

[<Tests>]
let tests =
    testList
        "renderer"
        [
            test "local and dev differ only where the environment says so: image, hot reload, limits" {
                let local = rendered "local"
                let dev = rendered "dev"
                Expect.equal (Map.keys local |> List.ofSeq) (Map.keys dev |> List.ofSeq) "the same files"

                Expect.stringContains local["backend-deployment.yaml"] "image: todo/backend:dev" "local builds"
                Expect.stringContains local["backend-deployment.yaml"] "mountPath: /src" "source mounted"
                Expect.stringContains local["backend-deployment.yaml"] "readOnlyRootFilesystem: false" "SDK writes"

                Expect.stringContains
                    dev["backend-deployment.yaml"]
                    "image: crplatform21784576.azurecr.io/todo/backend:55fb0bd"
                    "the commit's signed image"

                Expect.stringContains dev["backend-deployment.yaml"] "readOnlyRootFilesystem: true" "read-only"
                Expect.isFalse (dev["backend-deployment.yaml"].Contains "hostPath") "no source mount in the cloud"

                // Everything that isn't the environment's business is identical.
                for file in [ "backend-service.yaml"; "backend-route.yaml" ] do
                    Expect.equal local[file] dev[file] file
            }

            test "every service runs at least two instances, on different nodes, hardened (D57)" {
                let deployment = (rendered "dev")["backend-deployment.yaml"]
                Expect.stringContains deployment "replicas: 2" "two instances"
                Expect.stringContains deployment "topologyKey: kubernetes.io/hostname" "spread over nodes"
                Expect.stringContains deployment "nodeTaintsPolicy: Honor" "only nodes it can run on count"
                Expect.stringContains deployment "startupProbe:" "a slow start is not a restart"
                Expect.stringContains deployment "runAsNonRoot: true" "non-root"
                Expect.stringContains deployment "allowPrivilegeEscalation: false" "no escalation"
                Expect.stringContains deployment "- ALL" "no capabilities"
            }

            test "only a service that declares the database may reach it: by name locally, by range in Azure (D70)" {
                let local = rendered "local"
                Expect.stringContains local["backend-network.yaml"] "matchName: todo-todo" "the database"
                Expect.stringContains local["backend-network.yaml"] "matchPattern: '*'" "DNS inspected"
                Expect.isFalse (local["worker-network.yaml"].Contains "toFQDNs") "the worker has no database"
                let dev = rendered "dev"
                Expect.stringContains dev["backend-network.yaml"] "- 10.20.2.0/24" "the database subnet"
                Expect.isFalse (dev["backend-network.yaml"].Contains "toFQDNs") "no names (paid add-on in AKS)"
                Expect.isFalse (dev["backend-network.yaml"].Contains "matchPattern") "no DNS inspection"
            }

            test "health is on its own port, which only the probes use: no Service port, no route (D73)" {
                let local = rendered "local"
                Expect.stringContains local["backend-deployment.yaml"] "port: 8081" "probes on the health port"
                Expect.isFalse (local["backend-service.yaml"].Contains "8081") "the Service doesn't expose it"
                Expect.isFalse (local["backend-network.yaml"].Contains "8081") "no rule lets anyone in on it"
            }

            test "the health port must differ from the API port" {
                match App.parse (app.Replace("port: 8081, live", "port: 8080, live")) with
                | Ok _ -> failtest "expected an error"
                | Error errors -> Expect.stringContains (String.concat "\n" errors) "'health.port'" "health.port"
            }

            test "routes go through the gateway; a service without routes gets none" {
                let local = rendered "local"
                Expect.stringContains local["backend-route.yaml"] "value: /api" "/api"
                Expect.stringContains local["backend-route.yaml"] "namespace: gateway" "the gateway"
                Expect.isFalse (local.ContainsKey "worker-route.yaml") "no route"
            }

            test "a platform.yaml is refused with every problem at once" {
                let broken =
                    app
                        .Replace("min: 2, max: 4", "min: 1, max: 4")
                        .Replace("database: todo", "database: other")
                        .Replace("[ /, /api ]", "[ api ]")

                match App.parse broken with
                | Ok _ -> failtest "expected errors"
                | Error errors ->
                    let all = String.concat "\n" errors
                    Expect.stringContains all "below 2 (D57)" "min replicas"
                    Expect.stringContains all "'other' is not declared" "unknown database"
                    Expect.stringContains all "must start with /" "route"
            }

            test "the plan says which database each service may reach, for the network tests (D66)" {
                let plan = Render.plan (App.parse app |> ok) (environment "local") "dev"
                let byName = plan.services |> List.map (fun s -> s.name, s.database) |> Map.ofList
                Expect.equal byName["backend"] (Some "todo") "the backend's database"
                Expect.equal byName["worker"] None "the worker has none"
            }

            test "the environment says whether network decisions are recorded, and the plan passes it on (D71)" {
                Expect.isTrue (environment "local").networkFlows "Hubble locally"
                Expect.isFalse (environment "dev").networkFlows "dev decides with Phase 3 (ACNS)"
                Expect.isTrue (Render.plan (App.parse app |> ok) (environment "local") "dev").networkFlows "in the plan"
            }

            test "every service is named the same in traces, metrics and logs: its name within its app (D71)" {
                let deployment = (rendered "local")["backend-deployment.yaml"]
                Expect.stringContains deployment "value: backend" "OTEL_SERVICE_NAME"
                Expect.stringContains deployment "service.namespace=todo,deployment.environment.name=local" "the app"
                Expect.stringContains deployment "telemetry.platform/logs: otlp" "its own logs, not twice"
            }

            test "local and dev pin the same Kubernetes minor version (D61)" {
                Expect.equal (environment "local").kubernetesVersion (environment "dev").kubernetesVersion "1.37"
            }
        ]
