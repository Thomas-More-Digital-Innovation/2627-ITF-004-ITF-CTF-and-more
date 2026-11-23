using System.Security.Cryptography;
using System.Text;
using Pulumi;
using Pulumi.ProxmoxVE;
using Pulumi.ProxmoxVE.Inputs;
using Pulumi.Random;
using DownloadFile = Pulumi.ProxmoxVE.Download.File;
using DownloadFileArgs = Pulumi.ProxmoxVE.Download.FileArgs;

namespace Infrastructure.Pulumi
{
    /// <summary>
    /// Settings for a "kali-lab" environment. appsettings section "KaliLab" holds the defaults;
    /// every property can be overridden per environment through its runtime config (camelCase JSON keys).
    /// </summary>
    public class KaliLabOptions
    {
        public const string SectionName = "KaliLab";

        /// <summary>Number of Kali VMs, one per student (student01..studentNN).</summary>
        public int VmCount { get; set; } = 1;
        public int Cores { get; set; } = 2;
        public int MemoryMb { get; set; } = 4096;
        /// <summary>Must be at least the size of the Kali cloud image disk (12 GiB).</summary>
        public int DiskGb { get; set; } = 40;
        /// <summary>"seabios" or "ovmf".</summary>
        public string Bios { get; set; } = "seabios";

        /// <summary>Kali generic cloud image (.tar.xz). The backend extracts and converts it to qcow2 before uploading.</summary>
        public string KaliImageUrl { get; set; } = "https://kali.download/cloud-images/current/kali-linux-2026.3-cloud-genericcloud-amd64.tar.xz";
        /// <summary>Packages installed by cloud-init on first boot (desktop, tools, RDP server).</summary>
        public string[] Packages { get; set; } = ["qemu-guest-agent", "kali-desktop-xfce", "kali-linux-default", "xrdp"];
        public string StudentPrefix { get; set; } = "student";

        public int GuacamoleCores { get; set; } = 2;
        public int GuacamoleMemoryMb { get; set; } = 4096;
        public int GuacamoleDiskGb { get; set; } = 20;
        public string GuacamoleImageUrl { get; set; } = "https://cloud.debian.org/images/cloud/trixie/latest/debian-13-genericcloud-amd64.qcow2";
        public string GuacamoleVersion { get; set; } = "1.5.5";

        /// <summary>Proxmox storage with the "import" content type enabled (for the cloud images).</summary>
        public string ImportDatastoreId { get; set; } = "local";
        /// <summary>Proxmox storage with the "snippets" content type enabled (for cloud-init user-data).</summary>
        public string SnippetDatastoreId { get; set; } = "local";
        public string[] DnsServers { get; set; } = ["1.1.1.1", "8.8.8.8"];
    }

    /// <summary>
    /// N Kali desktops (one per student) plus a Guacamole VM that gives each student browser access
    /// (RDP) to their own machine. Addressing inside the environment's /16:
    ///   gateway .0.1, Guacamole .0.10, Kali VMs .1.1 .. .1.N
    /// </summary>
    public static class KaliLabProgram
    {
        public const string EnvironmentType = "kali-lab";
        public const int MaxVms = 250;

        public static Dictionary<string, object?> Run(ProxmoxOptions px, int vlanId, string cidr, string envName, KaliLabOptions opts, string kaliImagePath)
        {
            if (opts.VmCount < 1 || opts.VmCount > MaxVms)
                throw new ArgumentOutOfRangeException(nameof(opts.VmCount), $"vmCount must be between 1 and {MaxVms}.");

            var nodes = px.EffectiveNodes;
            var octets = cidr.Split('/')[0].Split('.');
            var net = $"{octets[0]}.{octets[1]}";
            var prefix = cidr.Contains('/') ? cidr.Split('/')[1] : "16";
            var gateway = $"{net}.0.1";
            var guacIp = $"{net}.0.10";

            // Images. "import" storage is per node, so every node hosting a Kali VM gets its own upload.
            var kaliNodes = Enumerable.Range(0, opts.VmCount).Select(i => nodes[i % nodes.Count]).Distinct().ToList();
            var kaliImages = kaliNodes.ToDictionary(node => node, node => new FileLegacy($"kali-image-{node}", new FileLegacyArgs
            {
                ContentType = "import",
                DatastoreId = opts.ImportDatastoreId,
                NodeName = node,
                SourceFile = new FileLegacySourceFileArgs
                {
                    Path = kaliImagePath,
                    FileName = $"{envName}-{Path.GetFileName(kaliImagePath)}",
                },
                TimeoutUpload = 3600,
            }));

            var guacNode = nodes[0];
            var debianImage = new DownloadFile("guacamole-image", new DownloadFileArgs
            {
                ContentType = "import",
                DatastoreId = opts.ImportDatastoreId,
                NodeName = guacNode,
                Url = opts.GuacamoleImageUrl,
                FileName = $"{envName}-guacamole-base.qcow2",
                OverwriteUnmanaged = true,
            });

            // Credentials
            RandomPassword Pw(string name) => new(name, new RandomPasswordArgs { Length = 16, Special = false });
            var adminPw = Pw("guacamole-admin-password");
            var dbPw = Pw("guacamole-db-password");

            var students = Enumerable.Range(1, opts.VmCount).Select(n =>
            {
                var user = $"{opts.StudentPrefix}{n:D2}";
                return new Student(user, $"{envName}-kali-{n:D2}", $"{net}.1.{n}", Pw($"{user}-password"));
            }).ToList();

            // Kali VMs
            var kaliVms = students.Select((s, i) =>
            {
                var node = nodes[i % nodes.Count];
                var userData = new FileLegacy($"{s.VmName}-user-data", new FileLegacyArgs
                {
                    ContentType = "snippets",
                    DatastoreId = opts.SnippetDatastoreId,
                    NodeName = node,
                    SourceRaw = new FileLegacySourceRawArgs
                    {
                        FileName = $"{s.VmName}-user-data.yaml",
                        Data = s.Password.BcryptHash.Apply(hash => KaliUserData(s.VmName, hash, opts.Packages)),
                    },
                });

                return CreateVm(px, opts, node, vlanId, s.VmName, "kali",
                    kaliImages[node].Id, opts.Cores, opts.MemoryMb, opts.DiskGb, opts.Bios,
                    $"{s.Ip}/{prefix}", gateway, userData.Id);
            }).ToList();

            // Guacamole VM
            var guacUserData = new FileLegacy("guacamole-user-data", new FileLegacyArgs
            {
                ContentType = "snippets",
                DatastoreId = opts.SnippetDatastoreId,
                NodeName = guacNode,
                SourceRaw = new FileLegacySourceRawArgs
                {
                    FileName = $"{envName}-guacamole-user-data.yaml",
                    Data = Output.Tuple(adminPw.Result, adminPw.BcryptHash, dbPw.Result, Output.All(students.Select(s => s.Password.Result)))
                        .Apply(t => GuacamoleUserData($"{envName}-guacamole", opts.GuacamoleVersion, t.Item1, t.Item2, t.Item3,
                            students.Select((s, i) => (s.Username, s.VmName, s.Ip, t.Item4[i])).ToList())),
                },
            });

            var guacVm = CreateVm(px, opts, guacNode, vlanId, $"{envName}-guacamole", "guacamole",
                debianImage.Id, opts.GuacamoleCores, opts.GuacamoleMemoryMb, opts.GuacamoleDiskGb, "seabios",
                $"{guacIp}/{prefix}", gateway, guacUserData.Id);

            return new Dictionary<string, object?>
            {
                ["guacamoleUrl"] = $"http://{guacIp}/",
                ["guacamoleAdminUser"] = "guacadmin",
                ["guacamoleAdminPassword"] = Output.CreateSecret(adminPw.Result),
                ["students"] = Output.CreateSecret(Output.All(students.Select(s => s.Password.Result)).Apply(pws =>
                    students.Select((s, i) => new Dictionary<string, object>
                    {
                        ["username"] = s.Username,
                        ["password"] = pws[i],
                        ["vm"] = s.VmName,
                        ["ip"] = s.Ip,
                    }).ToList())),
                ["vmIds"] = Output.All(kaliVms.Append(guacVm).Select(v => v.Id)),
            };
        }

        private sealed record Student(string Username, string VmName, string Ip, RandomPassword Password);

        private static string KaliUserData(string hostname, string passwordHash, string[] packages) => $$"""
            #cloud-config
            hostname: {{hostname}}
            manage_etc_hosts: true
            users:
              - name: student
                groups: [sudo]
                shell: /bin/bash
                lock_passwd: false
                passwd: '{{passwordHash}}'
            ssh_pwauth: true
            package_update: true
            packages:
            {{string.Join("\n", packages.Select(p => $"  - {p}"))}}
            runcmd:
              - [sh, -c, 'echo xfce4-session > /home/student/.xsession && chown student:student /home/student/.xsession']
              - [sh, -c, 'getent group ssl-cert && adduser xrdp ssl-cert || true']
              - systemctl enable --now qemu-guest-agent
              - systemctl enable xrdp
              - systemctl restart xrdp
              - systemctl set-default graphical.target
            """;

        private static string GuacamoleUserData(string hostname, string version, string adminPassword, string adminHash, string dbPassword,
            List<(string User, string Vm, string Ip, string Password)> students)
        {
            var compose = $$"""
                services:
                  postgres:
                    image: postgres:16
                    restart: unless-stopped
                    environment:
                      POSTGRES_DB: guacamole
                      POSTGRES_USER: guacamole
                      POSTGRES_PASSWORD: "{{dbPassword}}"
                    volumes:
                      - ./initdb:/docker-entrypoint-initdb.d:ro
                      - pgdata:/var/lib/postgresql/data
                  guacd:
                    image: guacamole/guacd:{{version}}
                    restart: unless-stopped
                  guacamole:
                    image: guacamole/guacamole:{{version}}
                    restart: unless-stopped
                    depends_on: [postgres, guacd]
                    environment:
                      GUACD_HOSTNAME: guacd
                      POSTGRES_HOSTNAME: postgres
                      POSTGRES_DATABASE: guacamole
                      POSTGRES_USER: guacamole
                      POSTGRES_PASSWORD: "{{dbPassword}}"
                      WEBAPP_CONTEXT: ROOT
                    ports:
                      - "80:8080"
                volumes:
                  pgdata:
                """;

            return $$"""
                #cloud-config
                hostname: {{hostname}}
                manage_etc_hosts: true
                users:
                  - name: cyberkit
                    groups: [sudo]
                    shell: /bin/bash
                    lock_passwd: false
                    passwd: '{{adminHash}}'
                ssh_pwauth: true
                package_update: true
                packages:
                  - qemu-guest-agent
                  - docker.io
                  - docker-compose
                write_files:
                  - path: /opt/guacamole/docker-compose.yml
                    permissions: '0600'
                    content: |
                {{Indent(compose, 6)}}
                  - path: /opt/guacamole/initdb/02-cyberkit.sql
                    permissions: '0644'
                    content: |
                {{Indent(GuacamoleSql(adminPassword, students), 6)}}
                runcmd:
                  - systemctl enable --now qemu-guest-agent docker
                  - [sh, -c, 'docker run --rm guacamole/guacamole:{{version}} /opt/guacamole/bin/initdb.sh --postgresql > /opt/guacamole/initdb/01-schema.sql']
                  - [sh, -c, 'cd /opt/guacamole && (docker compose up -d || docker-compose up -d)']
                """;
        }

        /// <summary>
        /// Seeds Guacamole's database: new guacadmin password, one user per student with access to only
        /// their own RDP connection. Runs once, when Postgres initialises its data volume.
        /// </summary>
        private static string GuacamoleSql(string adminPassword, List<(string User, string Vm, string Ip, string Password)> students)
        {
            static string Q(string s) => "'" + s.Replace("'", "''") + "'";
            static (string Hash, string Salt) HashPw(string user, string password)
            {
                // Guacamole: hash = SHA256(password + uppercase hex of salt). Salt is derived (not random)
                // so the snippet, and therefore the VM, doesn't change on every `pulumi up`.
                var salt = SHA256.HashData(Encoding.UTF8.GetBytes($"cyberkit-guacamole:{user}:{password}"));
                var saltHex = Convert.ToHexString(salt);
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(password + saltHex));
                return (Convert.ToHexString(hash), saltHex);
            }

            var sb = new StringBuilder();
            var (ah, asalt) = HashPw("guacadmin", adminPassword);
            sb.AppendLine($"UPDATE guacamole_user SET password_hash = decode('{ah}', 'hex'), password_salt = decode('{asalt}', 'hex'), password_date = now()");
            sb.AppendLine("  WHERE entity_id = (SELECT entity_id FROM guacamole_entity WHERE name = 'guacadmin' AND type = 'USER');");

            foreach (var s in students)
            {
                var (h, salt) = HashPw(s.User, s.Password);
                sb.AppendLine($"INSERT INTO guacamole_entity (name, type) VALUES ({Q(s.User)}, 'USER');");
                sb.AppendLine($"INSERT INTO guacamole_user (entity_id, password_hash, password_salt, password_date)");
                sb.AppendLine($"  SELECT entity_id, decode('{h}', 'hex'), decode('{salt}', 'hex'), now() FROM guacamole_entity WHERE name = {Q(s.User)} AND type = 'USER';");
                sb.AppendLine($"INSERT INTO guacamole_connection (connection_name, protocol) VALUES ({Q(s.Vm)}, 'rdp');");
                sb.AppendLine("INSERT INTO guacamole_connection_parameter (connection_id, parameter_name, parameter_value)");
                sb.AppendLine("  SELECT c.connection_id, p.name, p.value FROM guacamole_connection c, (VALUES");
                sb.AppendLine($"    ('hostname', {Q(s.Ip)}), ('port', '3389'), ('username', 'student'), ('password', {Q(s.Password)}),");
                sb.AppendLine("    ('security', 'any'), ('ignore-cert', 'true'), ('resize-method', 'display-update')) AS p(name, value)");
                sb.AppendLine($"  WHERE c.connection_name = {Q(s.Vm)};");
                foreach (var who in new[] { s.User, "guacadmin" })
                {
                    sb.AppendLine("INSERT INTO guacamole_connection_permission (entity_id, connection_id, permission)");
                    sb.AppendLine($"  SELECT e.entity_id, c.connection_id, 'READ' FROM guacamole_entity e, guacamole_connection c");
                    sb.AppendLine($"  WHERE e.name = {Q(who)} AND e.type = 'USER' AND c.connection_name = {Q(s.Vm)};");
                }
            }
            return sb.ToString();
        }

        private static string Indent(string text, int spaces) =>
            string.Join("\n", text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => l.Length == 0 ? l : new string(' ', spaces) + l));

        private static VmLegacy CreateVm(ProxmoxOptions px, KaliLabOptions opts, string node, int vlanId, string name, string role,
            Input<string> imageId, int cores, int memory, int diskGb, string bios, string ipWithCidr, string gateway, Input<string> userDataId)
        {
            var tags = new List<string> { role, KaliLabProgram.EnvironmentType };
            tags.AddRange(px.Tags);
            var uefi = string.Equals(bios, "ovmf", StringComparison.OrdinalIgnoreCase);

            return new VmLegacy(name, new VmLegacyArgs
            {
                Name = name,
                NodeName = node,
                Tags = tags,
                // The agent only exists once cloud-init has installed it (the Kali desktop install takes a
                // while), and IPs are static anyway, so don't make `pulumi up` wait for it.
                Agent = new VmLegacyAgentArgs { Enabled = true, Timeout = "1m", WaitForIp = new VmLegacyAgentWaitForIpArgs { Disabled = true } },
                Machine = "q35",
                Bios = uefi ? "ovmf" : "seabios",
                EfiDisk = uefi ? new VmLegacyEfiDiskArgs { DatastoreId = px.DatastoreId, FileFormat = "raw", Type = "4m" } : null!,
                Cpu = new VmLegacyCpuArgs { Sockets = 1, Cores = cores, Type = "x86-64-v2" },
                Memory = new VmLegacyMemoryArgs { Dedicated = memory },
                ScsiHardware = "virtio-scsi-single",
                // Without this the provider adds a passthrough of the host's physical CD drive on ide3,
                // which fails to start on nodes that have none.
                Cdrom = new VmLegacyCdromArgs { FileId = "none", Interface = "ide3" },
                Disks =
                {
                    new VmLegacyDiskArgs
                    {
                        DatastoreId = px.DatastoreId,
                        ImportFrom = imageId,
                        Interface = "scsi0",
                        Size = diskGb,
                        Ssd = true,
                        Discard = "on",
                        Iothread = true,
                    },
                },
                SerialDevices = { new VmLegacySerialDeviceArgs { Device = "socket" } },
                NetworkDevices = { new VmLegacyNetworkDeviceArgs { Bridge = px.VnetName, VlanId = vlanId } },
                OperatingSystem = new VmLegacyOperatingSystemArgs { Type = "l26" },
                Initialization = new VmLegacyInitializationArgs
                {
                    DatastoreId = px.DatastoreId,
                    UserDataFileId = userDataId,
                    Dns = new VmLegacyInitializationDnsArgs { Servers = opts.DnsServers },
                    IpConfigs =
                    {
                        new VmLegacyInitializationIpConfigArgs
                        {
                            Ipv4 = new VmLegacyInitializationIpConfigIpv4Args { Address = ipWithCidr, Gateway = gateway },
                        },
                    },
                },
                BootOrders = { "scsi0" },
            });
        }
    }
}
