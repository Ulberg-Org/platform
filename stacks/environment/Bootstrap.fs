/// The bootstrap in the cluster (D85): a Job per database applies its roles as the server's Entra admin, once per
/// server, with the same SQL as locally (database/roles.sql), then Azure's sign-ins (database/azure.sql), run by
/// database/azure-roles.sh. What runs in the cluster goes with it: no deletes of its own when the environment is
/// destroyed.
module Platform.Environment.Bootstrap

open Pulumi
open Pulumi.FSharp
open Platform.Environment.Desired

/// Where the Job runs, the image it runs, and the files it runs (from database/).
type Settings =
    {
        ns: string
        image: string
        rolesSql: string
        azureSql: string
        rolesScript: string
    }

/// The Kubernetes service account the Job runs as, which the server's Entra admin trusts.
let account = "db-admin"

/// Argo CD and its root application (D67, D100), installed once per cluster by scripts/argocd.sh, the same as the
/// platform CLI does locally. Argo CD then manages itself and everything else from `repo`, so this never runs
/// again for the same cluster.
let argocd (stack: Stack) (aks: Aks.Deployed) (repo: string) =
    Pulumi.Command.Local.Command(
        "argocd",
        Pulumi.Command.Local.CommandArgs(
            Create = "./scripts/argocd.sh",
            Environment =
                inputMap
                    [
                        "KUBECONFIG_DATA", io aks.kubeconfig
                        "ENVIRONMENT", input stack.environment
                        "REPO", input repo
                    ],
            Triggers = inputList [ io (aks.cluster.Id.Apply box) ]
        ),
        // The plugin's renderer comes from our registry: the nodes must be allowed to pull first.
        CustomResourceOptions(DeletedWith = aks.cluster, DependsOn = inputList [ input (aks.acrPull :> Resource) ])
    )

let deploy (stack: Stack) (aks: Aks.Deployed) (postgres: Postgres.Deployed) (settings: Settings) =
    let inCluster =
        CustomResourceOptions(Provider = aks.kubernetes, DeletedWith = aks.cluster)

    let inNamespace (dependsOn: Resource list) =
        CustomResourceOptions(
            Provider = aks.kubernetes,
            DeletedWith = aks.cluster,
            DependsOn = inputList (List.map input dependsOn)
        )

    let rolesNs =
        Pulumi.Kubernetes.Core.V1.Namespace(
            settings.ns,
            Pulumi.Kubernetes.Types.Inputs.Core.V1.NamespaceArgs(
                Metadata = Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(Name = settings.ns)
            ),
            inCluster
        )

    let rolesAccount =
        Pulumi.Kubernetes.Core.V1.ServiceAccount(
            account,
            Pulumi.Kubernetes.Types.Inputs.Core.V1.ServiceAccountArgs(
                Metadata =
                    Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(
                        Name = account,
                        Namespace = settings.ns,
                        Annotations = inputMap [ "azure.workload.identity/client-id", io postgres.admin.ClientId ]
                    )
            ),
            inNamespace [ rolesNs ]
        )

    postgres.databases
    |> List.map (fun (d, appDatabase) ->
        let files =
            Pulumi.Kubernetes.Core.V1.ConfigMap(
                $"roles-{d.name}",
                Pulumi.Kubernetes.Types.Inputs.Core.V1.ConfigMapArgs(
                    Metadata = Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(Namespace = settings.ns),
                    Data =
                        inputMap
                            [
                                "roles.sql", input (AppDatabase.fill stack.environment d settings.rolesSql)
                                "azure.sql", input (AppDatabase.fill stack.environment d settings.azureSql)
                                "azure-roles.sh", input settings.rolesScript
                            ]
                ),
                inNamespace [ rolesNs ]
            )

        let env (name: string) (value: Input<string>) =
            input (Pulumi.Kubernetes.Types.Inputs.Core.V1.EnvVarArgs(Name = name, Value = value))

        let container =
            Pulumi.Kubernetes.Types.Inputs.Core.V1.ContainerArgs(
                Name = "roles",
                Image = settings.image,
                Command = inputList [ input "/bin/sh"; input "/job/azure-roles.sh" ],
                Env =
                    inputList
                        [
                            env "PGHOST" (io postgres.server.FullyQualifiedDomainName)
                            env "PGUSER" (input stack.names.databaseAdmin)
                            env "PGDATABASE" (input d.name)
                            env "PGSSLMODE" (input "require")
                        ],
                VolumeMounts =
                    inputList
                        [
                            input (
                                Pulumi.Kubernetes.Types.Inputs.Core.V1.VolumeMountArgs(
                                    Name = "job",
                                    MountPath = "/job"
                                )
                            )
                        ],
                SecurityContext =
                    Pulumi.Kubernetes.Types.Inputs.Core.V1.SecurityContextArgs(
                        AllowPrivilegeEscalation = false,
                        ReadOnlyRootFilesystem = true,
                        Capabilities =
                            Pulumi.Kubernetes.Types.Inputs.Core.V1.CapabilitiesArgs(Drop = inputList [ input "ALL" ])
                    )
            )

        let pod =
            Pulumi.Kubernetes.Types.Inputs.Core.V1.PodSpecArgs(
                ServiceAccountName = account,
                RestartPolicy = "Never",
                SecurityContext =
                    Pulumi.Kubernetes.Types.Inputs.Core.V1.PodSecurityContextArgs(
                        RunAsNonRoot = true,
                        RunAsUser = 70,
                        SeccompProfile =
                            Pulumi.Kubernetes.Types.Inputs.Core.V1.SeccompProfileArgs(Type = "RuntimeDefault")
                    ),
                Containers = inputList [ input container ],
                Volumes =
                    inputList
                        [
                            input (
                                Pulumi.Kubernetes.Types.Inputs.Core.V1.VolumeArgs(
                                    Name = "job",
                                    ConfigMap =
                                        Pulumi.Kubernetes.Types.Inputs.Core.V1.ConfigMapVolumeSourceArgs(
                                            Name = io (files.Metadata.Apply(_.Name))
                                        )
                                )
                            )
                        ]
            )

        Pulumi.Kubernetes.Batch.V1.Job(
            $"roles-{d.name}",
            Pulumi.Kubernetes.Types.Inputs.Batch.V1.JobArgs(
                Metadata =
                    Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(
                        Namespace = settings.ns,
                        // A new server (each start) is a new Job; the same server keeps its roles.
                        Annotations = inputMap [ "platform/server", io postgres.server.Id ]
                    ),
                Spec =
                    Pulumi.Kubernetes.Types.Inputs.Batch.V1.JobSpecArgs(
                        BackoffLimit = 3,
                        Template =
                            Pulumi.Kubernetes.Types.Inputs.Core.V1.PodTemplateSpecArgs(
                                Metadata =
                                    Pulumi.Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs(
                                        Labels = inputMap [ "azure.workload.identity/use", input "true" ]
                                    ),
                                Spec = pod
                            )
                    )
            ),
            // pgaudit's extension needs the restarted server.
            inNamespace
                [
                    rolesAccount
                    postgres.administrator
                    postgres.adminFederation
                    appDatabase
                    postgres.restart
                ]
        ))
