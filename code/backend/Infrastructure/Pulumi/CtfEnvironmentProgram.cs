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
        public string NodeName { get; set; } = "pve";
        public string VnetName { get; set; } = "envs";
        public string DatastoreId { get; set; } = "sas-disks";
        public string[] Tags { get; set; } = ["pulumi"];
    }

    public class CtfClusterOptions
    {
        public const string SectionName = "CtfCluster";

        public string CertManagerVersion { get; set; } = "v1.17.1";
        public string ArgoCdVersion { get; set; } = "7.8.2";
        public string ChallengesRepoUrl { get; set; } = "https://gitlab.com/it-factory-thomas-more/SKIL3/25-26/bsides-ctf/challenges.git";
        public string? GitlabUsername { get; set; }
        public string? GitlabToken { get; set; }
        public string CtfdAdminUser { get; set; } = "admin";
        public string CtfdAdminEmail { get; set; } = "admin@ctf.local";
        public string CtfdAdminPassword { get; set; } = "admin";
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
    }

    /// <summary>
    /// C# Pulumi port of terraform/environments/ctf (Talos Kubernetes cluster on Proxmox).
    /// </summary>
    public static class CtfEnvironmentProgram
    {
        private const string TalosVersion = "v1.14.4";
        private const string TalosSchematicId = "ce4c980550dd2ab1b17bbf2b08801c7eb59418eafe8f279833297925d67c7515";
        private const string TalosIsoUrl = $"https://factory.talos.dev/image/{TalosSchematicId}/{TalosVersion}/nocloud-amd64.iso";

        private sealed class ArgoCdApplicationResourceArgs : K8s.ApiExtensions.CustomResourceArgs
        {
            [Input("spec", required: true)]
            public InputMap<object> Spec { get; set; } = new();

            public ArgoCdApplicationResourceArgs() : base("argoproj.io/v1alpha1", "Application")
            {
            }
        }

        private static string BuildNodePatch(string ipWithPrefix, string gatewayIp) =>
            $$"""
            {
              "machine": {
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
                  "image": "factory.talos.dev/installer/{{TalosSchematicId}}:{{TalosVersion}}",
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

            var iso = new DownloadFile("talos_iso", new DownloadFileArgs
            {
                ContentType = "iso",
                DatastoreId = "local",
                NodeName = px.NodeName,
                Url = TalosIsoUrl,
                FileName = $"talos-{TalosVersion}-nocloud-qemu-guest-agent.iso",
                OverwriteUnmanaged = true,
            });

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
                .Select(i => CreateVm(px, iso, vlanId, $"{envName}-master-{i + 1}", $"master-{i}", 600 + i, "master", cores: 2, memory: 4096, diskSize: 20, $"{masterIps[i]}/{prefix}", gatewayIp))
                .ToList();
            var workerVms = Enumerable.Range(0, workers)
                .Select(i => CreateVm(px, iso, vlanId, $"{envName}-worker-{i + 1}", $"worker-{i}", 610 + i, "worker", cores: 4, memory: 8192, diskSize: 40, $"{workerIps[i]}/{prefix}", gatewayIp))
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

            Output<string> MachineConfig(string type, string ip) => Machines.GetConfiguration.Invoke(new Machines.GetConfigurationInvokeArgs
            {
                ClusterName = clusterName,
                ClusterEndpoint = $"https://{firstMasterIp}:6443",
                MachineType = type,
                MachineSecrets = SecretsInput(secrets),
                ConfigPatches = { BuildNodePatch($"{ip}/{prefix}", gatewayIp) },
            }).Apply(r => r.MachineConfiguration);

            var cpApplies = masterVms.Select((vm, i) => new ConfigurationApply($"controlplane-{i}", new ConfigurationApplyArgs
            {
                ClientConfiguration = ClientCfg(secrets),
                MachineConfigurationInput = MachineConfig("controlplane", masterIps[i]),
                Endpoint = masterIps[i],
                Node = masterIps[i],
            }, new CustomResourceOptions { DependsOn = { vm } })).ToList();

            var workerApplies = workerVms.Select((vm, i) => new ConfigurationApply($"worker-{i}", new ConfigurationApplyArgs
            {
                ClientConfiguration = ClientCfg(secrets),
                MachineConfigurationInput = MachineConfig("worker", workerIps[i]),
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
                KubeConfig = kubeconfig.KubeconfigRaw,
            }, new CustomResourceOptions { DependsOn = { kubeconfig } });

            var certManagerRelease = new Release("cert-manager", new ReleaseArgs
            {
                Name = "cert-manager",
                Chart = "cert-manager",
                Version = clusterOpts.CertManagerVersion,
                Namespace = "cert-manager",
                CreateNamespace = true,
                RepositoryOpts = new RepositoryOptsArgs
                {
                    Repo = "https://charts.jetstack.io",
                },
                Values = new InputMap<object>
                {
                    ["crds"] = new Dictionary<string, object>
                    {
                        ["enabled"] = true,
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
            }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { certManagerRelease } });

            var ctfNamespace = new K8s.Core.V1.Namespace("ctf-namespace", new K8s.Types.Inputs.Core.V1.NamespaceArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "ctf",
                    Labels =
                    {
                        ["pod-security.kubernetes.io/enforce"] = "privileged",
                        ["pod-security.kubernetes.io/audit"] = "privileged",
                        ["pod-security.kubernetes.io/warn"] = "privileged",
                    },
                },
            }, new CustomResourceOptions { Provider = k8sProvider, DependsOn = { k8sProvider } });

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

                var syncPolicy = new Dictionary<string, object>();
                if (app.AutomatedSync)
                {
                    syncPolicy["automated"] = new Dictionary<string, object>
                    {
                        ["prune"] = true,
                        ["selfHeal"] = true,
                    };
                }
                if (app.CreateNamespace)
                {
                    syncPolicy["syncOptions"] = new[] { "CreateNamespace=true" };
                }

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

        private static VmLegacy CreateVm(ProxmoxOptions px, DownloadFile iso, int vlanId, string name, string resourceName, int vmId, string role,
            int cores, int memory, int diskSize, string staticIpWithCidr, string gatewayIp)
        {
            var tags = new List<string> { "talos", role, "ctf" };
            tags.AddRange(px.Tags);

            return new VmLegacy(resourceName, new VmLegacyArgs
            {
                Name = name,
                NodeName = px.NodeName,
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
