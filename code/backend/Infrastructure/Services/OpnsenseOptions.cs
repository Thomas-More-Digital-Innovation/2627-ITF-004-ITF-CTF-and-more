namespace Infrastructure.Services
{
    public class OpnsenseOptions
    {
        public const string SectionName = "Opnsense";

        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
        public bool AllowInsecure { get; set; } = true;
        public string ParentInterface { get; set; } = "vtnet0";
    }
}

