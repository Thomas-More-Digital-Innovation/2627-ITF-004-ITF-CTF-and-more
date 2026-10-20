using Pulumi;
using Pulumi.ProxmoxVE;
using Pulumi.ProxmoxVE.Inputs;
using DownloadFile = Pulumi.ProxmoxVE.Download.File;
using DownloadFileArgs = Pulumi.ProxmoxVE.Download.FileArgs;
using TalosClient = Pulumiverse.Talos.Client;
using TalosInputs = Pulumiverse.Talos.Machine.Inputs;
using Pulumiverse.Talos.Machine;
using Machines = Pulumiverse.Talos.Machine;
using TalosSecrets = Pulumiverse.Talos.Machine.Secrets;

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

    /// <summary>
    /// C# Pulumi port of terraform/environments/ctf (Talos Kubernetes cluster on Proxmox).
    /// </summary>
    public static class CtfEnvironmentProgram
    {
        private const string TalosVersion = "v1.14.4";
        private const string TalosSchematicId = "ce4c980550dd2ab1b17bbf2b08801c7eb59418eafe8f279833297925d67c7515";
        private const string TalosIsoUrl = $"https://factory.talos.dev/image/{TalosSchematicId}/{TalosVersion}/nocloud-amd64.iso";

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
                }
              },
              "cluster": {
                "network": {
                  "podSubnets": ["172.16.0.0/16"],
                  "serviceSubnets": ["172.17.0.0/16"]
                }
              }
            }
            """;

        public static Dictionary<string, object?> Run(ProxmoxOptions px, int vlanId, string cidr, string envName = "ctf", int masters = 3, int workers = 6)
        {
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

            _ = workerVms.Select((vm, i) => new ConfigurationApply($"worker-{i}", new ConfigurationApplyArgs
            {
                ClientConfiguration = ClientCfg(secrets),
                MachineConfigurationInput = MachineConfig("worker", workerIps[i]),
                Endpoint = workerIps[i],
                Node = workerIps[i],
            }, new CustomResourceOptions { DependsOn = { vm } })).ToList();

            _ = new Bootstrap("this", new BootstrapArgs
            {
                ClientConfiguration = ClientCfg(secrets),
                Endpoint = firstMasterIp,
                Node = firstMasterIp,
            }, new CustomResourceOptions { DependsOn = cpApplies.Cast<Resource>().ToList() });

            return new Dictionary<string, object?>
            {
                ["talosconfig"] = Output.CreateSecret(clientConfig.Apply(c => c.TalosConfig)),
                ["kubeconfig"] = $"Run 'talosctl kubeconfig --nodes {firstMasterIp} --endpoints {firstMasterIp}' after bootstrap",
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
