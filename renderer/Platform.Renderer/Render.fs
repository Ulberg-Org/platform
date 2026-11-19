/// The renderer (D60, D62): an app's platform.yaml + an environment → plain Kubernetes manifests. Pure: the
/// same input always gives the same files, and nothing per environment is written by hand.
module Platform.Renderer.Render

open Platform.Renderer.App
open Platform.Renderer.Environment
open Platform.Renderer.Manifest

/// Where `database` of `app` lives in this environment.
let databaseHost (environment: Environment) (app: App) (database: Name) =
    environment.databaseHost.Replace("{app}", Name.value app.name).Replace("{database}", Name.value database)

let private metadata (name: string) (ns: string) (labels: (string * Node) list) =
    "metadata", fields [ "name", text name; "namespace", text ns; "labels", fields labels ]

let private labels (app: App) (s: Service) =
    [
        "app.kubernetes.io/name", text (Name.value s.name)
        "app.kubernetes.io/part-of", text (Name.value app.name)
    ]

let private selector (s: Service) =
    fields [ "app.kubernetes.io/name", text (Name.value s.name) ]

/// The image this environment runs: the app's `dev` target built locally (develop), or the commit's signed
/// image from the registry (release, D54).
let private image (app: App) (env: Environment) (s: Service) (tag: string) =
    match env.mode with
    | Develop -> $"{Name.value app.name}/{Name.value s.name}:dev"
    | Release registry -> $"{registry}/{s.image}:{tag}"

let private probe (path: string) (port: int) =
    fields
        [
            "httpGet", fields [ "path", text path; "port", Number port ]
            "periodSeconds", Number 5
        ]

let private env (app: App) (environment: Environment) (s: Service) =
    items
        [
            yield
                fields
                    [
                        "name", text "INSTANCE_NAME"
                        "valueFrom", fields [ "fieldRef", fields [ "fieldPath", text "metadata.name" ] ]
                    ]
            yield
                fields
                    [
                        "name", text "OTEL_EXPORTER_OTLP_ENDPOINT"
                        "value", text environment.telemetryEndpoint
                    ]
            match s.database with
            | Some db ->
                // The connection string (with a password locally, an Entra identity in Azure) comes from a
                // Secret the platform creates for the app's role (§9.2); the app only names its database.
                yield
                    fields
                        [
                            "name", text "Database__ConnectionString"
                            "valueFrom",
                            fields
                                [
                                    "secretKeyRef",
                                    fields [ "name", text $"{Name.value db}-app"; "key", text "connectionString" ]
                                ]
                        ]
            | None -> ()
        ]

let private deployment (app: App) (environment: Environment) (tag: string) (s: Service) =
    let develop = environment.mode = Develop
    let name = Name.value s.name

    let container =
        fields
            [
                yield "name", text name
                yield "image", text (image app environment s tag)
                yield "imagePullPolicy", text (if develop then "Never" else "IfNotPresent")
                yield "ports", items [ fields [ "containerPort", Number s.port ] ]
                yield "env", env app environment s
                yield "livenessProbe", probe s.live s.port
                yield "readinessProbe", probe s.ready s.port

                yield
                    "resources",
                    fields
                        [
                            yield "requests", fields [ "cpu", text s.cpu; "memory", text s.memory ]
                            // The SDK compiling with hot reload needs far more than the app; release is capped.
                            if not develop then
                                yield "limits", fields [ "memory", text s.memory ]
                        ]

                yield
                    "securityContext",
                    fields
                        [
                            "allowPrivilegeEscalation", Flag false
                            // The SDK writes build output while developing; release images write nothing.
                            "readOnlyRootFilesystem", Flag(not develop)
                            "capabilities", fields [ "drop", items [ text "ALL" ] ]
                        ]

                if develop then
                    yield
                        "volumeMounts",
                        items
                            [
                                fields [ "name", text "source"; "mountPath", text "/src"; "readOnly", Flag true ]
                                fields [ "name", text "scratch"; "mountPath", text "/tmp" ]
                            ]
            ]

    let volumes =
        if develop then
            [
                "volumes",
                items
                    [
                        // The app repo, mounted into every kind node by the platform CLI (D61: hot reload).
                        fields [ "name", text "source"; "hostPath", fields [ "path", text "/src" ] ]
                        fields [ "name", text "scratch"; "emptyDir", fields [] ]
                    ]
            ]
        else
            []

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
                            "selector", fields [ "matchLabels", selector s ]
                            "template",
                            fields
                                [
                                    "metadata", fields [ "labels", fields (labels app s) ]
                                    "spec",
                                    fields
                                        [
                                            yield
                                                "securityContext",
                                                fields
                                                    [
                                                        "runAsNonRoot", Flag true
                                                        "seccompProfile", fields [ "type", text "RuntimeDefault" ]
                                                    ]
                                            // D57: the instances sit on different nodes, so one node failing
                                            // never takes the service down.
                                            yield
                                                "topologySpreadConstraints",
                                                items
                                                    [
                                                        fields
                                                            [
                                                                "maxSkew", Number 1
                                                                "topologyKey", text "kubernetes.io/hostname"
                                                                "whenUnsatisfiable", text "DoNotSchedule"
                                                                "labelSelector", fields [ "matchLabels", selector s ]
                                                            ]
                                                    ]
                                            yield "containers", items [ container ]
                                            yield! volumes
                                        ]
                                ]
                        ]
                ]
    }

let private service (app: App) (s: Service) =
    let name = Name.value s.name

    {
        file = $"{name}-service.yaml"
        content =
            fields
                [
                    "apiVersion", text "v1"
                    "kind", text "Service"
                    metadata name (Name.value app.name) (labels app s)
                    "spec",
                    fields
                        [
                            "selector", selector s
                            "ports", items [ fields [ "port", Number s.port; "targetPort", Number s.port ] ]
                        ]
                ]
    }

let private route (app: App) (environment: Environment) (s: Service) =
    let name = Name.value s.name

    {
        file = $"{name}-route.yaml"
        content =
            fields
                [
                    "apiVersion", text "gateway.networking.k8s.io/v1"
                    "kind", text "HTTPRoute"
                    metadata name (Name.value app.name) (labels app s)
                    "spec",
                    fields
                        [
                            "parentRefs",
                            items
                                [
                                    fields
                                        [
                                            "name", text environment.gatewayName
                                            "namespace", text environment.gatewayNamespace
                                        ]
                                ]
                            "rules",
                            items
                                [
                                    for path in s.routes ->
                                        fields
                                            [
                                                "matches",
                                                items
                                                    [
                                                        fields
                                                            [
                                                                "path",
                                                                fields [ "type", text "PathPrefix"; "value", text path ]
                                                            ]
                                                    ]
                                                "backendRefs",
                                                items [ fields [ "name", text name; "port", Number s.port ] ]
                                            ]
                                ]
                        ]
                ]
    }

/// Deny by default (AGENTS.md §2): nothing in the app's namespace talks to or from anything unless a rule
/// below allows it.
let private denyAll (app: App) =
    {
        file = "network-deny-all.yaml"
        content =
            fields
                [
                    "apiVersion", text "networking.k8s.io/v1"
                    "kind", text "NetworkPolicy"
                    metadata "deny-all" (Name.value app.name) []
                    "spec",
                    fields
                        [
                            "podSelector", fields []
                            "policyTypes", items [ text "Ingress"; text "Egress" ]
                        ]
                ]
    }

/// What a service may talk to (§9.1): in from the gateway on its port; out to DNS, to the telemetry collector
/// and, only if it declares one, to its database.
let private networkRules (app: App) (environment: Environment) (s: Service) =
    let name = Name.value s.name

    let port (n: int) =
        items
            [
                fields
                    [
                        "ports", items [ fields [ "port", text (string n); "protocol", text "TCP" ] ]
                    ]
            ]

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
                            "endpointSelector", fields [ "matchLabels", selector s ]
                            "ingress",
                            items
                                [
                                    fields
                                        [
                                            "fromEndpoints",
                                            items
                                                [
                                                    fields
                                                        [
                                                            "matchLabels",
                                                            fields
                                                                [
                                                                    "k8s:io.kubernetes.pod.namespace",
                                                                    text environment.gatewayNamespace
                                                                ]
                                                        ]
                                                ]
                                            "toPorts", port s.port
                                        ]
                                ]
                            "egress",
                            items
                                [
                                    yield
                                        fields
                                            [
                                                "toEndpoints",
                                                items
                                                    [
                                                        fields
                                                            [
                                                                "matchLabels",
                                                                fields
                                                                    [
                                                                        "k8s:io.kubernetes.pod.namespace",
                                                                        text "kube-system"
                                                                        "k8s-app", text "kube-dns"
                                                                    ]
                                                            ]
                                                    ]
                                                "toPorts",
                                                items
                                                    [
                                                        fields
                                                            [
                                                                "ports",
                                                                items
                                                                    [
                                                                        fields
                                                                            [
                                                                                "port", text "53"
                                                                                "protocol", text "ANY"
                                                                            ]
                                                                    ]
                                                                // DNS is inspected, so the database rule below can
                                                                // allow it by name.
                                                                "rules",
                                                                fields
                                                                    [
                                                                        "dns",
                                                                        items [ fields [ "matchPattern", text "*" ] ]
                                                                    ]
                                                            ]
                                                    ]
                                            ]
                                    yield
                                        fields
                                            [
                                                "toEndpoints",
                                                items
                                                    [
                                                        fields
                                                            [
                                                                "matchLabels",
                                                                fields
                                                                    [
                                                                        "k8s:io.kubernetes.pod.namespace",
                                                                        text "observability"
                                                                    ]
                                                            ]
                                                    ]
                                                "toPorts", port 4317
                                            ]
                                    match s.database with
                                    | Some db ->
                                        // The database is outside the cluster, locally and in Azure (D61).
                                        yield
                                            fields
                                                [
                                                    "toFQDNs",
                                                    items
                                                        [
                                                            fields
                                                                [ "matchName", text (databaseHost environment app db) ]
                                                        ]
                                                    "toPorts", port environment.databasePort
                                                ]
                                    | None -> ()
                                ]
                        ]
                ]
    }

let private ns (app: App) =
    {
        file = "namespace.yaml"
        content =
            fields
                [
                    "apiVersion", text "v1"
                    "kind", text "Namespace"
                    "metadata", fields [ "name", text (Name.value app.name) ]
                ]
    }

/// Every manifest for `app` in `environment`; `tag` is the image tag (the commit SHA) in release mode.
let render (app: App) (environment: Environment) (tag: string) : Manifest list =
    [
        yield ns app
        yield denyAll app
        for s in app.services do
            yield deployment app environment tag s
            yield service app s

            if not s.routes.IsEmpty then
                yield route app environment s

            yield networkRules app environment s
    ]

type PlannedService =
    {
        name: string
        build: string
        image: string
    }

type PlannedDatabase = { name: string; host: string }

/// What the platform CLI needs besides the manifests: which images to build and which databases to create,
/// from the validated model, so the CLI never parses platform.yaml itself.
type Plan =
    {
        app: string
        services: PlannedService list
        databases: PlannedDatabase list
    }

let plan (app: App) (environment: Environment) (tag: string) : Plan =
    {
        app = Name.value app.name
        services =
            [
                for s in app.services ->
                    {
                        name = Name.value s.name
                        build = s.build
                        image = image app environment s tag
                    }
            ]
        databases =
            [
                for db in app.databases ->
                    {
                        name = Name.value db
                        host = databaseHost environment app db
                    }
            ]
    }
