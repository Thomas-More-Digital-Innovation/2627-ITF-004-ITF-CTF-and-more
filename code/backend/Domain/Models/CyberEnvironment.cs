namespace Domain.Models
{
    public class CyberEnvironment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        /// <summary>Which Pulumi program deploys this environment: "ctf" or "kali-lab".</summary>
        public string Type { get; set; } = "ctf";
        /// <summary>Per-environment overrides of the type's appsettings defaults, as a JSON object.</summary>
        public string? Config { get; set; }
        public int VlanId { get; set; }
        public string Cidr { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
