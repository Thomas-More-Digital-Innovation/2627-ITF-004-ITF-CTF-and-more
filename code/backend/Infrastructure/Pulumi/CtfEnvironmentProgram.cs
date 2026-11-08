using Pulumi;
using Pulumi.ProxmoxVE;
using Pulumi.ProxmoxVE.Inputs;
using DownloadFile = Pulumi.ProxmoxVE.Download.File;
using DownloadFileArgs = Pulumi.ProxmoxVE.Download.FileArgs;
using TalosClient = Pulumiverse.Talos.Client;
using TalosCluster = Pulumiverse.Talos.Cluster;
using TalosInputs = Pulumiverse.Talos.Machine.Inputs;
using Pulumiverse.Talos.Machine;
using Machines = Pulumiverse.Talos.Machine;
using TalosSecrets = Pulumiverse.Talos.Machine.Secrets;
using K8s = Pulumi.Kubernetes;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Random;
using TalosSchematic = Pulumiverse.Talos.ImageFactory.Schematic;
using TalosSchematicArgs = Pulumiverse.Talos.ImageFactory.SchematicArgs;

namespace Infrastructure.Pulumi
{
    public class ProxmoxOptions
    {
        public const string SectionName = "Proxmox";

        public string Endpoint { get; set; } = string.Empty;
        public bool Insecure { get; set; } = true;
        public string? ApiToken { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        /// <summary>
        /// Proxmox nodes to spread VMs over (round-robin per role, so masters land on different nodes).
        /// A single entry puts everything on that node. DatastoreId and VnetName must exist on every node.
        /// </summary>
        public string[] NodeNames { get; set; } = [];

        public IReadOnlyList<string> EffectiveNodes =>
            NodeNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray() is { Length: > 0 } nodes
                ? nodes
                : throw new InvalidOperationException("Proxmox:NodeNames must contain at least one node (e.g. Proxmox__NodeNames__0=pve).");
        public string VnetName { get; set; } = "envs";
        public string DatastoreId { get; set; } = "sas-disks";
        public string[] Tags { get; set; } = ["pulumi"];
    }

    public class CtfClusterOptions
    {
        public const string SectionName = "CtfCluster";

        public string CiliumVersion { get; set; } = "1.20.4";
        public string ArgoCdVersion { get; set; } = "7.8.2";
        public string ChallengesRepoUrl { get; set; } = "https://gitlab.com/it-factory-thomas-more/SKIL3/25-26/bsides-ctf/challenges.git";
        public string? GitlabUsername { get; set; }
        public string? GitlabToken { get; set; }
        public string CtfdAdminUser { get; set; } = "admin";
        public string CtfdAdminEmail { get; set; } = "admin@ctf.local";
        public string CtfdAdminPassword { get; set; } = "admin";

        // VM sizing (memory in MiB, disk in GiB). Spread round-robin over Proxmox:NodeNames.
        public int MasterCores { get; set; } = 4;
        public int MasterMemoryMb { get; set; } = 8192;
        public int MasterDiskGb { get; set; } = 20;
        public int WorkerCores { get; set; } = 8;
        public int WorkerMemoryMb { get; set; } = 12288;
        public int WorkerDiskGb { get; set; } = 80;

        /// <summary>Public domain CTFd is served on (e.g. ctf.example.com). Empty = plain HTTP on any host.</summary>
        public string? Domain { get; set; }
        /// <summary>Contact address for the Let's Encrypt account.</summary>
        public string? AcmeEmail { get; set; }
        /// <summary>Use the Let's Encrypt staging CA (untrusted certs, no rate limits) while testing.</summary>
        public bool AcmeStaging { get; set; } = false;
        /// <summary>Cloudflare API token with Zone:Read and DNS:Edit on the domain's zone, for DNS-01 challenges.</summary>
        public string? CloudflareApiToken { get; set; }
        /// <summary>Repo/path of the Helm chart holding the domain-dependent objects (issuer, certificate, routes).</summary>
        public string EdgeChartRepoUrl { get; set; } = "https://github.com/Thomas-More-Digital-Innovation/2627-ITF-004-ITF-CTF-and-more.git";
        public string EdgeChartPath { get; set; } = "code/k8s/ctf-edge";
        public string EdgeChartRevision { get; set; } = "HEAD";
        public List<ArgoCdApplicationConfig> Applications { get; set; } = [];

        public static List<ArgoCdApplicationConfig> DefaultApplications =>
        [
            new ArgoCdApplicationConfig
            {
                Name = "ctf-apps",
                Namespace = "argocd",
                Project = "default",
                RepoUrl = "https://github.com/Thomas-More-Digital-Innovation/2627-ITF-004-ITF-CTF-and-more.git",
                TargetRevision = "HEAD",
                Path = "code/k8s/ctf",
                DestinationServer = "https://kubernetes.default.svc",
                DestinationNamespace = "ctf",
                CreateNamespace = true,
                AutomatedSync = true,
            }
        ];
    }

    public class ArgoCdApplicationConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Namespace { get; set; } = "argocd";
        public string Project { get; set; } = "default";
        public string RepoUrl { get; set; } = string.Empty;
        public string TargetRevision { get; set; } = "HEAD";
        public string? Path { get; set; }
        public string? Chart { get; set; }
        public string DestinationServer { get; set; } = "https://kubernetes.default.svc";
        public string DestinationNamespace { get; set; } = "default";
        public bool CreateNamespace { get; set; } = true;
        public bool AutomatedSync { get; set; } = true;
        /// <summary>Optional Helm values (spec.source.helm.valuesObject) for chart or Helm-directory sources.</summary>
        public Dictionary<string, object>? HelmValues { get; set; }
    }

    /// <summary>
    /// C# Pulumi program for the CTF environment (Talos Kubernetes cluster on Proxmox).
    /// </summary>
    public static class CtfEnvironmentProgram
    {
        private const string TalosVersion = "v1.14.4";

        // Image Factory schematic: qemu-guest-agent for Proxmox, iscsi-tools + util-linux-tools for Longhorn.
        private const string TalosSchematicYaml = """
            customization:
              systemExtensions:
                officialExtensions:
                  - siderolabs/iscsi-tools
                  - siderolabs/qemu-guest-agent
                  - siderolabs/util-linux-tools
            """;

        private sealed class ArgoCdApplicationResourceArgs : K8s.ApiExtensions.CustomResourceArgs
        {
            [Input("spec", required: true)]
            public InputMap<object> Spec { get; set; } = new();

            public ArgoCdApplicationResourceArgs() : base("argoproj.io/v1alpha1", "Application")
            {
            }
        }

        // user.max_user_namespaces: Talos defaults it to 0 (KSPP hardening). Challenge pods run with
        // hostUsers=false so root in a challenge is unprivileged on the node, which needs user namespaces.
        // kubelet.extraMounts: Longhorn keeps replica data in /var/lib/longhorn and needs it bind-mounted
        // into the kubelet with rshared propagation (Talos guide for Longhorn).
        // registries: registry.ctf-build.svc.cluster.local (in-cluster registry, code/k8s/ctf/build-infra.yaml) is pulled
        // over plain HTTP from the registry NodePort on the node's own IP, which works before cluster DNS.
        // nodeLabels: topology.kubernetes.io/zone = Proxmox host, so Longhorn places replicas (and the
        // scheduler spreads pods) across physical machines rather than just across VMs.
        private static string BuildNodePatch(string ipWithPrefix, string gatewayIp, string schematicId, string proxmoxNode) =>
            $$"""
            {
              "machine": {
                "sysctls": {
                  "user.max_user_namespaces": "11255"
                },
                "nodeLabels": {
                  "topology.kubernetes.io/zone": "{{proxmoxNode}}"
                },
                "registries": {
                  "mirrors": {
                    "registry.ctf-build.svc.cluster.local": {
                      "endpoints": ["http://{{ipWithPrefix.Split('/')[0]}}:30500"],
                      "overridePath": false
                    }
                  }
                },
                "kubelet": {
                  "extraMounts": [
                    {
                      "destination": "/var/lib/longhorn",
                      "type": "bind",
                      "source": "/var/lib/longhorn",
                      "options": ["bind", "rshared", "rw"]
                    }
                  ]
                },
                "network": {
                  "nameservers": ["1.1.1.1", "8.8.8.8"],
                  "interfaces": [
                    {
                      "deviceSelector": { "busPath": "0*" },
                      "addresses": ["{{ipWithPrefix}}"],
                      "routes": [
                        { "network": "0.0.0.0/0", "gateway": "{{gatewayIp}}" }
                      ]
                    }
                  ]
                },
                "install": {
                  "disk": "/dev/sda",
                  "image": "factory.talos.dev/installer/{{schematicId}}:{{TalosVersion}}",
                  "wipe": true
                },
                "files": [
                  {
                    "path": "/etc/cri/conf.d/20-customization.part",
                    "op": "create",
                    "content": "[plugins.\"io.containerd.cri.v1.images\"]\n  discard_unpacked_layers = false\n"
                  }
                ]
              },
              "cluster": {
                "network": {
                  "cni": { "name": "none" },
                  "podSubnets": ["172.16.0.0/16"],
                  "serviceSubnets": ["172.17.0.0/16"]
                }
              }
            }
            """;

        public static Dictionary<string, object?> Run(ProxmoxOptions px, int vlanId, string cidr, string envName = "ctf", int masters = 3, int workers = 6, CtfClusterOptions? clusterOptions = null)
        {
            var clusterOpts = clusterOptions ?? new CtfClusterOptions();
            var applications = clusterOpts.Applications.Count > 0
                ? clusterOpts.Applications
                : CtfClusterOptions.DefaultApplications;

            var schematic = new TalosSchematic("talos-schematic", new TalosSchematicArgs
            {
                SchematicData = TalosSchematicYaml,
            });

            // "local" storage is per node, so every node that hosts a VM gets its own copy of the ISO.
            // The first node keeps the original resource name so existing stacks don't re-download it.
            var nodes = px.EffectiveNodes;
            var isos = nodes.Select((node, n) => new DownloadFile(n == 0 ? "talos_iso" : $"talos_iso-{node}", new DownloadFileArgs
            {
                ContentType = "iso",
                DatastoreId = "local",
                NodeName = node,
                Url = schematic.Id.Apply(id => $"https://factory.talos.dev/image/{id}/{TalosVersion}/nocloud-amd64.iso"),
                FileName = schematic.Id.Apply(id => $"talos-{TalosVersion}-{id[..12]}-nocloud.iso"),
                OverwriteUnmanaged = true,
            })).ToList();

            var cidrParts = cidr.Split('/');
            var ipParts = cidrParts[0].Split('.');
            var baseSubnet = $"{ipParts[0]}.{ipParts[1]}.{ipParts[2]}";
            var prefix = cidrParts.Length > 1 ? cidrParts[1] : "16";
            var gatewayIp = $"{baseSubnet}.1";

            // Static IP allocations:
            // Masters: 10.<vlan>.0.11, .12, .13
            // Workers: 10.<vlan>.0.21.. .26
            var masterIps = Enumerable.Range(0, masters).Select(i => $"{baseSubnet}.{11 + i}").ToList();
            var workerIps = Enumerable.Range(0, workers).Select(i => $"{baseSubnet}.{21 + i}").ToList();

            var masterVms = Enumerable.Range(0, masters)
                .Select(i => CreateVm(px, nodes[i % nodes.Count], isos[i % nodes.Count], vlanId, $"{envName}-master-{i + 1}", $"master-{i}", 600 + i, "master", cores: clusterOpts.MasterCores, memory: clusterOpts.MasterMemoryMb, diskSize: clusterOpts.MasterDiskGb, $"{masterIps[i]}/{prefix}", gatewayIp))
                .ToList();
            var workerVms = Enumerable.Range(0, workers)
                .Select(i => CreateVm(px, nodes[i % nodes.Count], isos[i % nodes.Count], vlanId, $"{envName}-worker-{i + 1}", $"worker-{i}", 610 + i, "worker", cores: clusterOpts.WorkerCores, memory: clusterOpts.WorkerMemoryMb, diskSize: clusterOpts.WorkerDiskGb, $"{workerIps[i]}/{prefix}", gatewayIp))
                .ToList();

            var clusterName = $"{envName}-cluster";
            var secrets = new TalosSecrets("this");
            var firstMasterIp = masterIps[0];

            var clientConfig = TalosClient.GetConfiguration.Invoke(new TalosClient.GetConfigurationInvokeArgs
            {
                ClusterName = clusterName,
                ClientConfiguration = secrets.ClientConfiguration.Apply(c => new TalosClient.Inputs.GetConfigurationClientConfigurationInputArgs
                {
                    CaCertificate = c.CaCertificate,
                    ClientCertificate = c.ClientCertificate,
                    ClientKey = c.ClientKey,
                }),
                Endpoints = masterIps,
            });

            Output<string> MachineConfig(string type, string ip, string proxmoxNode) => Machines.GetConfiguration.Invoke(new Machines.GetConfigurationInvokeArgs
            {
                ClusterName = clusterName,
                ClusterEndpoint = $"https://{firstMasterIp}:6443",
                MachineType = type,
                MachineSecrets = SecretsInput(secrets),
                ConfigPatches = { schematic.Id.Apply(id => BuildNodePatch($"{ip}/{prefix}", gatewayIp, id, proxmoxNode)) },
            }).Apply(r => r.MachineConfiguration);

            var cpApplies = masterVms.Select((vm, i) => new ConfigurationApply($"controlplane-{i}", new ConfigurationApplyArgs
            {
                ClientConfiguration = ClientCfg(secrets),
                MachineConfigurationInput = MachineConfig("controlplane", masterIps[i], nodes[i % nodes.Count]),
                Endpoint = masterIps[i],
                Node = masterIps[i],
            }, new CustomResourceOptions { DependsOn = { vm } })).ToList();

            var workerApplies = workerVms.Select((vm, i) => new ConfigurationApply($"worker-{i}", new ConfigurationApplyArgs
            {
                ClientConfiguration = ClientCfg(secrets),
                MachineConfigurationInput = MachineConfig("worker", workerIps[i], nodes[i % nodes.Count]),
                Endpoint = workerIps[i],
                Node = workerIps[i],
            }, new CustomResourceOptions { DependsOn = { vm } })).ToList();

            var bootstrap = new Bootstrap("this", new BootstrapArgs
            {
                ClientConfiguration = ClientCfg(secrets),
                Endpoint = firstMasterIp,
                Node = firstMasterIp,
            }, new CustomResourceOptions { DependsOn = cpApplies.Cast<Resource>().ToList() });

            var kubeconfig = new TalosCluster.Kubeconfig("this", new TalosCluster.KubeconfigArgs
            {
                ClientConfiguration = new TalosCluster.Inputs.KubeconfigClientConfigurationArgs
                {
                    CaCertificate = secrets.ClientConfiguration.Apply(c => c.CaCertificate),
                    ClientCertificate = secrets.ClientConfiguration.Apply(c => c.ClientCertificate),
                    ClientKey = secrets.ClientConfiguration.Apply(c => c.ClientKey),
                },
                Endpoint = firstMasterIp,
                Node = firstMasterIp,
            }, new CustomResourceOptions
            {
                DependsOn = new List<Resource> { bootstrap }.Concat(workerApplies).ToList(),
            });

            var k8sProvider = new K8s.Provider("k8s-provider", new K8s.ProviderArgs
            {
                // Bootstrap returns once etcd is started; kube-apiserver comes up some time later.
                // Hand the kubeconfig to the provider only after the API server answers.
                KubeConfig = kubeconfig.KubeconfigRaw.Apply(async kc =>
                {
                    await WaitForApiServerAsync(firstMasterIp, TimeSpan.FromMinutes(15));
                    return kc;
                }),
                EnableServerSideApply = true,
                UpsertExistingObjects = true,
            }, new CustomResourceOptions { DependsOn = { kubeconfig } });

            // CNI: Talos is configured with cni=none, so nodes stay NotReady until Cilium is up.
            // Cilium enforces the NetworkPolicies in code/k8s/ctf (Flannel silently ignores them).
            // Values follow the Talos guide: kube-proxy stays, cgroup2 is already mounted by Talos,
            // and the agent gets explicit capabilities instead of SYS_MODULE (Talos forbids module loading).
            var ciliumRelease = new Release("cilium", new ReleaseArgs
            {
                Name = "cilium",
                Chart = "cilium",
                Version = clusterOpts.CiliumVersion,
                Namespace = "kube-system",
                RepositoryOpts = new RepositoryOptsArgs
                {
                    Repo = "https://helm.cilium.io/",
                },
                Values = new InputMap<object>
                {
                    ["ipam"] = new Dictionary<string, object> { ["mode"] = "kubernetes" },
                    ["kubeProxyReplacement"] = false,
                    ["cgroup"] = new Dictionary<string, object>
                    {
                        ["autoMount"] = new Dictionary<string, object> { ["enabled"] = false },
                        ["hostRoot"] = "/sys/fs/cgroup",
                    },
                    ["securityContext"] = new Dictionary<string, object>
                    {
                        ["capabilities"] = new Dictionary<string, object>
                        {
                            ["ciliumAgent"] = new[] { "CHOWN", "KILL", "NET_ADMIN", "NET_RAW", "IPC_LOCK", "SYS_ADMIN", "SYS_RESOURCE", "DAC_OVERRIDE", "FOWNER", "SETGID", "SETUID" },
                            ["cleanCiliumState"] = new[] { "NET_ADMIN", "SYS_ADMIN", "SYS_RESOURCE" },
                        },
                    },
                },
            }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { k8sProvider } });

            var argoCdRelease = new Release("argocd", new ReleaseArgs
            {
                Name = "argocd",
                Chart = "argo-cd",
                Version = clusterOpts.ArgoCdVersion,
                Namespace = "argocd",
                CreateNamespace = true,
                RepositoryOpts = new RepositoryOptsArgs
                {
                    Repo = "https://argoproj.github.io/argo-helm",
                },
                // Since ArgoCD 1.8 child Applications report no health, so sync waves made of
                // Applications pass immediately. Restore the health check so app-of-apps waves
                // (operators -> CRs -> ctfd -> ctfd-bootstrap) really wait for each other.
                Values = new InputMap<object>
                {
                    ["configs"] = new Dictionary<string, object>
                    {
                        ["cm"] = new Dictionary<string, object>
                        {
                            ["resource.customizations.health.argoproj.io_Application"] = @"hs = {}
hs.status = ""Progressing""
hs.message = """"
if obj.status ~= nil and obj.status.health ~= nil then
  hs.status = obj.status.health.status
  if obj.status.health.message ~= nil then
    hs.message = obj.status.health.message
  end
end
return hs
",
                        },
                    },
                },
            }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { ciliumRelease } });

            // Bootstrap secrets are the only in-cluster objects created here besides ArgoCD and the
            // app-of-apps: they cannot live in git. Their namespaces are created bare (name only);
            // labels such as Pod Security levels are owned by code/k8s/ctf/namespace.yaml via ArgoCD.
            // With server-side apply Pulumi only manages metadata.name, so the two never fight.
            var ctfNamespace = BareNamespace("ctf", k8sProvider, ciliumRelease);
            var ctfBuildNamespace = BareNamespace("ctf-build", k8sProvider, ciliumRelease);

            // Builder pods only get the GitLab credentials they need (no CTFd admin credentials).
            _ = new K8s.Core.V1.Secret("ctf-build-gitlab-credentials", new K8s.Types.Inputs.Core.V1.SecretArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "gitlab-credentials",
                    Namespace = "ctf-build",
                },
                StringData =
                {
                    ["GITLAB_USERNAME"] = clusterOpts.GitlabUsername ?? "oauth2",
                    ["GITLAB_TOKEN"] = Output.CreateSecret(clusterOpts.GitlabToken ?? string.Empty),
                },
            }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { ctfBuildNamespace } });

            _ = new K8s.Core.V1.Secret("ctf-gitlab-credentials", new K8s.Types.Inputs.Core.V1.SecretArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "gitlab-credentials",
                    Namespace = "ctf",
                },
                StringData =
                {
                    ["CHALLENGES_REPO_URL"] = clusterOpts.ChallengesRepoUrl,
                    ["GITLAB_USERNAME"] = clusterOpts.GitlabUsername ?? "oauth2",
                    ["GITLAB_TOKEN"] = Output.CreateSecret(clusterOpts.GitlabToken ?? string.Empty),
                    ["CTFD_ADMIN_USER"] = clusterOpts.CtfdAdminUser,
                    ["CTFD_ADMIN_EMAIL"] = clusterOpts.CtfdAdminEmail,
                    ["CTFD_ADMIN_PASSWORD"] = Output.CreateSecret(clusterOpts.CtfdAdminPassword),
                },
            }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { ctfNamespace } });

            // cert-manager reads the Cloudflare token from its own namespace (ClusterIssuer secret refs
            // resolve against the cert-manager namespace).
            if (!string.IsNullOrWhiteSpace(clusterOpts.CloudflareApiToken))
            {
                var certManagerNamespace = BareNamespace("cert-manager", k8sProvider, ciliumRelease);
                _ = new K8s.Core.V1.Secret("cloudflare-api-token", new K8s.Types.Inputs.Core.V1.SecretArgs
                {
                    Metadata = new ObjectMetaArgs { Name = "cloudflare-api-token", Namespace = "cert-manager" },
                    StringData = { ["api-token"] = Output.CreateSecret(clusterOpts.CloudflareApiToken) },
                }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { certManagerNamespace } });
            }

            // Database and CTFd session credentials. RandomPassword lives in the stack state, so the values
            // stay stable across `pulumi up` runs (a new value would lock CTFd out of MariaDB).
            var dbRootPassword = new RandomPassword("ctfd-db-root-password", new RandomPasswordArgs { Length = 32, Special = false });
            var dbPassword = new RandomPassword("ctfd-db-password", new RandomPasswordArgs { Length = 32, Special = false });
            var ctfdSecretKey = new RandomPassword("ctfd-secret-key", new RandomPasswordArgs { Length = 64, Special = false });

            // Consumed by the MariaDB custom resource in code/k8s/ctf/ctfd-db.yaml.
            _ = new K8s.Core.V1.Secret("ctfd-db", new K8s.Types.Inputs.Core.V1.SecretArgs
            {
                Metadata = new ObjectMetaArgs { Name = "ctfd-db", Namespace = "ctf" },
                StringData =
                {
                    ["root-password"] = dbRootPassword.Result,
                    ["password"] = dbPassword.Result,
                },
            }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { ctfNamespace } });

            // Consumed by the CTFd chart via secret.existingSecret. ctfd-db-primary is the Service
            // mariadb-operator creates that always points at the Galera node currently taking writes.
            _ = new K8s.Core.V1.Secret("ctfd-secrets", new K8s.Types.Inputs.Core.V1.SecretArgs
            {
                Metadata = new ObjectMetaArgs { Name = "ctfd-secrets", Namespace = "ctf" },
                StringData =
                {
                    ["SECRET_KEY"] = ctfdSecretKey.Result,
                    ["DATABASE_URL"] = dbPassword.Result.Apply(pw =>
                        $"mysql+pymysql://ctfd:{pw}@ctfd-db-primary.ctf.svc.cluster.local:3306/ctfd"),
                },
            }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { ctfNamespace } });

            // Domain-dependent objects (ClusterIssuer, Certificate, Traefik routes for CTFd) come from a small
            // Helm chart in git; the domain itself is only known here, so it is passed as Helm values.
            var edgeValues = new Dictionary<string, object>
            {
                ["domain"] = clusterOpts.Domain ?? string.Empty,
                ["acme"] = new Dictionary<string, object>
                {
                    ["email"] = clusterOpts.AcmeEmail ?? string.Empty,
                    ["staging"] = clusterOpts.AcmeStaging,
                    ["cloudflare"] = !string.IsNullOrWhiteSpace(clusterOpts.CloudflareApiToken),
                },
            };
            applications = applications.Append(new ArgoCdApplicationConfig
            {
                Name = "ctf-edge",
                RepoUrl = clusterOpts.EdgeChartRepoUrl,
                TargetRevision = clusterOpts.EdgeChartRevision,
                Path = clusterOpts.EdgeChartPath,
                DestinationNamespace = "ctf",
                HelmValues = edgeValues,
            }).ToList();

            foreach (var app in applications)
            {
                var source = new Dictionary<string, object>
                {
                    ["repoURL"] = app.RepoUrl,
                    ["targetRevision"] = app.TargetRevision,
                };
                if (!string.IsNullOrWhiteSpace(app.Path))
                {
                    source["path"] = app.Path;
                }
                if (!string.IsNullOrWhiteSpace(app.Chart))
                {
                    source["chart"] = app.Chart;
                }
                if (app.HelmValues is { Count: > 0 })
                {
                    source["helm"] = new Dictionary<string, object> { ["valuesObject"] = app.HelmValues };
                }

                var syncPolicy = new Dictionary<string, object>();
                if (app.AutomatedSync)
                {
                    syncPolicy["automated"] = new Dictionary<string, object>
                    {
                        ["prune"] = true,
                        ["selfHeal"] = true,
                    };
                }
                // SkipDryRunOnMissingResource: apps may reference CRDs (Certificate, IngressRoute, MariaDB)
                // that another app is still installing; the retry picks them up once they exist.
                syncPolicy["syncOptions"] = app.CreateNamespace
                    ? new[] { "CreateNamespace=true", "SkipDryRunOnMissingResource=true" }
                    : new[] { "SkipDryRunOnMissingResource=true" };
                syncPolicy["retry"] = new Dictionary<string, object>
                {
                    ["limit"] = 10,
                    ["backoff"] = new Dictionary<string, object> { ["duration"] = "30s", ["factor"] = 2, ["maxDuration"] = "10m" },
                };

                _ = new K8s.ApiExtensions.CustomResource($"argocd-app-{app.Name}", new ArgoCdApplicationResourceArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Name = app.Name,
                        Namespace = app.Namespace,
                        Finalizers = { "resources-finalizer.argocd.argoproj.io" },
                    },
                    Spec = new InputMap<object>
                    {
                        ["project"] = app.Project,
                        ["source"] = source,
                        ["destination"] = new Dictionary<string, object>
                        {
                            ["server"] = app.DestinationServer,
                            ["namespace"] = app.DestinationNamespace,
                        },
                        ["syncPolicy"] = syncPolicy,
                    },
                }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { argoCdRelease } });
            }

            return new Dictionary<string, object?>
            {
                ["talosconfig"] = Output.CreateSecret(clientConfig.Apply(c => c.TalosConfig)),
                ["kubeconfig"] = Output.CreateSecret(kubeconfig.KubeconfigRaw),
            };
        }

        private static K8s.Core.V1.Namespace BareNamespace(string name, K8s.Provider provider, Resource dependsOn) =>
            new($"{name}-namespace", new K8s.Types.Inputs.Core.V1.NamespaceArgs
            {
                Metadata = new ObjectMetaArgs { Name = name },
            }, new CustomResourceOptions { Provider = provider, DependsOn = { dependsOn } });

        private static async Task WaitForApiServerAsync(string host, TimeSpan timeout)
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                // Only probing liveness; the cluster CA is not trusted by this host.
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            })
            { Timeout = TimeSpan.FromSeconds(5) };

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    // Any HTTP answer (200, or 401/403 without credentials) means the API server is serving.
                    using var resp = await http.GetAsync($"https://{host}:6443/readyz");
                    Console.WriteLine($"kube-apiserver on {host} answered ({(int)resp.StatusCode}).");
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Console.WriteLine($"Waiting for kube-apiserver on {host}:6443...");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            throw new TimeoutException($"kube-apiserver on {host}:6443 did not come up within {timeout}.");
        }

        private static Input<TalosInputs.ClientConfigurationArgs> ClientCfg(TalosSecrets secrets) =>
            new TalosInputs.ClientConfigurationArgs
            {
                CaCertificate = secrets.ClientConfiguration.Apply(c => c.CaCertificate),
                ClientCertificate = secrets.ClientConfiguration.Apply(c => c.ClientCertificate),
                ClientKey = secrets.ClientConfiguration.Apply(c => c.ClientKey),
            };

        private static Input<TalosInputs.MachineSecretsInputArgs> SecretsInput(TalosSecrets secrets)
        {
            var ms = secrets.MachineSecrets;
            return new TalosInputs.MachineSecretsInputArgs
            {
                Cluster = new TalosInputs.ClusterInputArgs
                {
                    Id = ms.Apply(m => m.Cluster.Id),
                    Secret = ms.Apply(m => m.Cluster.Secret),
                },
                Secrets = new TalosInputs.KubernetesSecretsInputArgs
                {
                    AescbcEncryptionSecret = ms.Apply(m => m.Secrets.AescbcEncryptionSecret),
                    BootstrapToken = ms.Apply(m => m.Secrets.BootstrapToken),
                    SecretboxEncryptionSecret = ms.Apply(m => m.Secrets.SecretboxEncryptionSecret),
                },
                Trustdinfo = new TalosInputs.TrustdInfoInputArgs { Token = ms.Apply(m => m.Trustdinfo.Token) },
                Certs = new TalosInputs.CertificatesInputArgs
                {
                    Etcd = new TalosInputs.CertificateInputArgs { Cert = ms.Apply(m => m.Certs.Etcd.Cert), Key = ms.Apply(m => m.Certs.Etcd.Key) },
                    K8s = new TalosInputs.CertificateInputArgs { Cert = ms.Apply(m => m.Certs.K8s.Cert), Key = ms.Apply(m => m.Certs.K8s.Key) },
                    K8sAggregator = new TalosInputs.CertificateInputArgs { Cert = ms.Apply(m => m.Certs.K8sAggregator.Cert), Key = ms.Apply(m => m.Certs.K8sAggregator.Key) },
                    K8sServiceaccount = new TalosInputs.KeyInputArgs { Key = ms.Apply(m => m.Certs.K8sServiceaccount.Key) },
                    Os = new TalosInputs.CertificateInputArgs { Cert = ms.Apply(m => m.Certs.Os.Cert), Key = ms.Apply(m => m.Certs.Os.Key) },
                },
            };
        }

        private static VmLegacy CreateVm(ProxmoxOptions px, string nodeName, DownloadFile iso, int vlanId, string name, string resourceName, int vmId, string role,
            int cores, int memory, int diskSize, string staticIpWithCidr, string gatewayIp)
        {
            var tags = new List<string> { "talos", role, "ctf" };
            tags.AddRange(px.Tags);

            return new VmLegacy(resourceName, new VmLegacyArgs
            {
                Name = name,
                NodeName = nodeName,
                VmId = vmId,
                Tags = tags,
                Agent = new VmLegacyAgentArgs { Enabled = true },
                Machine = "q35",
                Bios = "ovmf",
                Cpu = new VmLegacyCpuArgs { Sockets = 1, Cores = cores, Type = "x86-64-v2" },
                Memory = new VmLegacyMemoryArgs { Dedicated = memory },
                Cdrom = new VmLegacyCdromArgs { FileId = iso.Id, Interface = "ide0" },
                Disks =
                {
                    new VmLegacyDiskArgs
                    {
                        DatastoreId = px.DatastoreId,
                        Interface = "scsi0",
                        Size = diskSize,
                        Ssd = true,
                        FileFormat = "raw",
                    },
                },
                EfiDisk = new VmLegacyEfiDiskArgs { DatastoreId = px.DatastoreId, FileFormat = "raw", Type = "4m" },
                NetworkDevices =
                {
                    new VmLegacyNetworkDeviceArgs { Bridge = px.VnetName, VlanId = vlanId },
                },
                OperatingSystem = new VmLegacyOperatingSystemArgs { Type = "l26" },
                Initialization = new VmLegacyInitializationArgs
                {
                    DatastoreId = px.DatastoreId,
                    IpConfigs =
                    {
                        new VmLegacyInitializationIpConfigArgs
                        {
                            Ipv4 = new VmLegacyInitializationIpConfigIpv4Args
                            {
                                Address = staticIpWithCidr,
                                Gateway = gatewayIp
                            },
                        },
                    },
                },
                BootOrders = { "scsi0", "ide0" },
            });
        }
    }
}
