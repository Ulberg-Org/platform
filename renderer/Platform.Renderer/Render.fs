/// The renderer (D60, D62): an app's platform.yaml + an environment → plain Kubernetes manifests. Pure: the
/// same input always gives the same files, and nothing per environment is written by hand.
module Platform.Renderer.Render

open Platform.Renderer.App
open Platform.Renderer.Environment
open Platform.Renderer.Manifest

/// Where `database` of `app` lives in this environment.
let databaseHost (environment: Environment) (app: App) (database: Name) =
    environment.databaseHost.Replace("{app}", Name.value app.name).Replace("{database}", Name.value database)

/// The image this environment runs: the app's `dev` target built locally (develop), or the commit's signed
/// image from the registry (release, D54).
let private image (app: App) (environment: Environment) (s: Service) (tag: string) =
    match environment.mode with
    | Develop -> $"{Name.value app.name}/{Name.value s.name}:dev"
    | Release registry -> $"{registry}/{s.image}:{tag}"

/// How a container runs in this environment, decided once so the manifests below never branch on the mode.
/// Develop (D61): the source mounted for hot reload, and a writable filesystem and no memory cap for the SDK
/// compiling. Release: read-only and capped.
type private Runtime =
    {
        pullPolicy: string
        readOnlyRoot: bool
        memoryLimit: Node option
        mounts: Node option
        volumes: Node option
    }

let private runtime (environment: Environment) (s: Service) =
    match environment.mode with
    | Develop ->
        {
            pullPolicy = "Never"
            readOnlyRoot = false
            memoryLimit = None
            mounts =
                Some(
                    items
                        [
                            fields [ "name", text "source"; "mountPath", text "/src"; "readOnly", Flag true ]
                            fields [ "name", text "scratch"; "mountPath", text "/tmp" ]
                        ]
                )
            volumes =
                Some(
                    items
                        [
                            // The app repo, mounted into every kind node by the platform CLI.
                            fields [ "name", text "source"; "hostPath", fields [ "path", text "/src" ] ]
                            fields [ "name", text "scratch"; "emptyDir", fields [] ]
                        ]
                )
        }
    | Release _ ->
        {
            pullPolicy = "IfNotPresent"
            readOnlyRoot = true
            memoryLimit = Some(fields [ "memory", text s.memory ])
            mounts = None
            volumes = None
        }

let private metadata (name: string) (ns: string) (labels: (string * Node) list) =
    "metadata", fields [ "name", text name; "namespace", text ns; "labels", fields labels ]

let private labels (app: App) (s: Service) =
    [
        "app.kubernetes.io/name", text (Name.value s.name)
        "app.kubernetes.io/part-of", text (Name.value app.name)
    ]

let private nameLabel (s: Service) =
    fields [ "app.kubernetes.io/name", text (Name.value s.name) ]

let private selector (s: Service) = fields [ "matchLabels", nameLabel s ]

let private inNamespace (ns: string) =
    fields [ "matchLabels", fields [ "k8s:io.kubernetes.pod.namespace", text ns ] ]

let private httpGet (path: string) (port: int) =
    "httpGet", fields [ "path", text path; "port", Number port ]

let private probe (path: string) (port: int) =
    fields [ httpGet path port; "periodSeconds", Number 5 ]

/// Liveness only starts once the app has started, so a slow start (the SDK compiling, D61) is never a restart.
let private startupProbe (path: string) (port: int) =
    fields [ httpGet path port; "periodSeconds", Number 2; "failureThreshold", Number 90 ]

let private variable (name: string) (value: string) =
    fields [ "name", text name; "value", text value ]

let private variableFrom (name: string) (source: string * Node) =
    fields [ "name", text name; "valueFrom", fields [ source ] ]

/// The connection string (with a password locally, an Entra identity in Azure) comes from a Secret the platform
/// creates for the app's role (§9.2); the app only names its database.
let private connectionString (db: Name) =
    variableFrom
        "Database__ConnectionString"
        ("secretKeyRef", fields [ "name", text $"{Name.value db}-app"; "key", text "connectionString" ])

let private deployment (app: App) (environment: Environment) (tag: string) (s: Service) =
    let name = Name.value s.name
    let runtime = runtime environment s

    let instanceName =
        variableFrom "INSTANCE_NAME" ("fieldRef", fields [ "fieldPath", text "metadata.name" ])

    let telemetry = variable "OTEL_EXPORTER_OTLP_ENDPOINT" environment.telemetryEndpoint
    let database = s.database |> Option.map connectionString |> Option.toList

    let resources =
        fields (
            [ "requests", fields [ "cpu", text s.cpu; "memory", text s.memory ] ]
            @ optional [ "limits", runtime.memoryLimit ]
        )

    let containerSecurity =
        fields
            [
                "allowPrivilegeEscalation", Flag false
                "readOnlyRootFilesystem", Flag runtime.readOnlyRoot
                "capabilities", fields [ "drop", items [ text "ALL" ] ]
            ]

    let container =
        fields (
            [
                "name", text name
                "image", text (image app environment s tag)
                "imagePullPolicy", text runtime.pullPolicy
                "ports", items [ fields [ "containerPort", Number s.port ] ]
                "env", items ([ instanceName; telemetry ] @ database)
                "startupProbe", startupProbe s.live s.healthPort
                "livenessProbe", probe s.live s.healthPort
                "readinessProbe", probe s.ready s.healthPort
                "resources", resources
                "securityContext", containerSecurity
            ]
            @ optional [ "volumeMounts", runtime.mounts ]
        )

    let podSecurity =
        fields
            [
                "runAsNonRoot", Flag true
                "seccompProfile", fields [ "type", text "RuntimeDefault" ]
            ]

    // D57: the instances sit on different nodes, so one node failing never takes the service down.
    let spread =
        fields
            [
                "maxSkew", Number 1
                "topologyKey", text "kubernetes.io/hostname"
                "whenUnsatisfiable", text "DoNotSchedule"
                // Nodes the service can't run on (tainted: the control plane, AKS's system pool) don't count.
                "nodeTaintsPolicy", text "Honor"
                "labelSelector", selector s
            ]

    let pod =
        fields (
            [
                "securityContext", podSecurity
                "topologySpreadConstraints", items [ spread ]
                "containers", items [ container ]
            ]
            @ optional [ "volumes", runtime.volumes ]
        )

    let template =
        fields [ "metadata", fields [ "labels", fields (labels app s) ]; "spec", pod ]

    {
        file = $"{name}-deployment.yaml"
        content =
            fields
                [
                    "apiVersion", text "apps/v1"
                    "kind", text "Deployment"
                    metadata name (Name.value app.name) (labels app s)
                    "spec",
                    fields
                        [
                            "replicas", Number s.minReplicas
                            "selector", selector s
                            "template", template
                        ]
                ]
    }

let private service (app: App) (s: Service) =
    let name = Name.value s.name
    let port = fields [ "port", Number s.port; "targetPort", Number s.port ]

    {
        file = $"{name}-service.yaml"
        content =
            fields
                [
                    "apiVersion", text "v1"
                    "kind", text "Service"
                    metadata name (Name.value app.name) (labels app s)
                    "spec", fields [ "selector", nameLabel s; "ports", items [ port ] ]
                ]
    }

let private route (app: App) (environment: Environment) (s: Service) =
    let name = Name.value s.name

    let gateway =
        fields
            [
                "name", text environment.gatewayName
                "namespace", text environment.gatewayNamespace
            ]

    let backend = fields [ "name", text name; "port", Number s.port ]

    let rule (path: string) =
        let prefix =
            fields [ "path", fields [ "type", text "PathPrefix"; "value", text path ] ]

        fields [ "matches", items [ prefix ]; "backendRefs", items [ backend ] ]

    {
        file = $"{name}-route.yaml"
        content =
            fields
                [
                    "apiVersion", text "gateway.networking.k8s.io/v1"
                    "kind", text "HTTPRoute"
                    metadata name (Name.value app.name) (labels app s)
                    "spec", fields [ "parentRefs", items [ gateway ]; "rules", items (s.routes |> List.map rule) ]
                ]
    }

/// A port rule; Cilium wants the port as text.
let private tcp (port: int) =
    items
        [
            fields
                [
                    "ports", items [ fields [ "port", text (string port); "protocol", text "TCP" ] ]
                ]
        ]

/// DNS, inspected when the database is allowed by name: Cilium learns the name's addresses from the answers.
let private dnsPorts (environment: Environment) =
    let port = "ports", items [ fields [ "port", text "53"; "protocol", text "ANY" ] ]

    let inspect =
        "rules", fields [ "dns", items [ fields [ "matchPattern", text "*" ] ] ]

    match environment.databaseAccess with
    | ByName -> fields [ port; inspect ]
    | ByRange _ -> fields [ port ]

/// Where a service's database is, as the network rule names it (D70).
let private databasePeer (environment: Environment) (app: App) (db: Name) =
    match environment.databaseAccess with
    | ByName -> "toFQDNs", items [ fields [ "matchName", text (databaseHost environment app db) ] ]
    | ByRange cidr -> "toCIDR", items [ text cidr ]

/// What a service may talk to (§9.1): in from the gateway on its port; out to DNS, to the telemetry collector
/// and, only if it declares one, to its database.
let private networkRules (app: App) (environment: Environment) (s: Service) =
    let name = Name.value s.name

    let fromGateway =
        fields
            [
                "fromEndpoints", items [ inNamespace environment.gatewayNamespace ]
                "toPorts", tcp s.port
            ]

    let kubeDns =
        fields
            [
                "matchLabels",
                fields
                    [
                        "k8s:io.kubernetes.pod.namespace", text "kube-system"
                        "k8s-app", text "kube-dns"
                    ]
            ]

    let toDns =
        fields [ "toEndpoints", items [ kubeDns ]; "toPorts", items [ dnsPorts environment ] ]

    let toTelemetry =
        fields [ "toEndpoints", items [ inNamespace "observability" ]; "toPorts", tcp 4317 ]

    // The database is outside the cluster, locally and in Azure (D61).
    let toDatabase (db: Name) =
        fields [ databasePeer environment app db; "toPorts", tcp environment.databasePort ]

    let database = s.database |> Option.map toDatabase |> Option.toList

    {
        file = $"{name}-network.yaml"
        content =
            fields
                [
                    "apiVersion", text "cilium.io/v2"
                    "kind", text "CiliumNetworkPolicy"
                    metadata name (Name.value app.name) (labels app s)
                    "spec",
                    fields
                        [
                            "endpointSelector", selector s
                            "ingress", items [ fromGateway ]
                            "egress", items ([ toDns; toTelemetry ] @ database)
                        ]
                ]
    }

/// A service's manifests; a service without routes gets no route.
let private serviceManifests (app: App) (environment: Environment) (tag: string) (s: Service) =
    let route =
        if s.routes.IsEmpty then
            None
        else
            Some(route app environment s)

    [ deployment app environment tag s; service app s ]
    @ Option.toList route
    @ [ networkRules app environment s ]

/// Every manifest for `app` in `environment`; `tag` is the image tag (the commit SHA) in release mode. The app's
/// namespace and its deny-all rule belong to the platform (Cluster.fs), so an app can't remove them.
let render (app: App) (environment: Environment) (tag: string) : Manifest list =
    List.collect (serviceManifests app environment tag) app.services

type PlannedService =
    {
        name: string
        build: string
        image: string
        /// The database its network rule allows, if any; the platform's network tests check it (D66).
        database: string option
    }

type PlannedDatabase = { name: string; host: string }

/// What the platform CLI needs besides the manifests: which images to build and which databases to create,
/// from the validated model, so the CLI never parses platform.yaml itself.
type Plan =
    {
        app: string
        services: PlannedService list
        databases: PlannedDatabase list
        /// Whether the bootstrap turns on Cilium's flow logs (Hubble), from the environment (D71).
        networkFlows: bool
    }

let plan (app: App) (environment: Environment) (tag: string) : Plan =
    let plannedService (s: Service) =
        {
            name = Name.value s.name
            build = s.build
            image = image app environment s tag
            database = s.database |> Option.map Name.value
        }

    let plannedDatabase (db: Name) =
        {
            name = Name.value db
            host = databaseHost environment app db
        }

    {
        app = Name.value app.name
        services = app.services |> List.map plannedService
        databases = app.databases |> List.map plannedDatabase
        networkFlows = environment.networkFlows
    }
