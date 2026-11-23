using System.Reflection;
using System.Text.Json;

namespace Infrastructure.Pulumi
{
    /// <summary>
    /// Applies a per-environment JSON config (stored on the environment record) on top of the
    /// appsettings defaults. Only the keys present in the JSON are replaced; everything else keeps
    /// its configured value. Unknown keys are rejected so typos don't silently fall back to defaults.
    /// </summary>
    public static class RuntimeConfig
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public static T Merge<T>(T defaults, string? json) where T : class, new()
        {
            var result = new T();
            var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .ToList();

            foreach (var p in props)
            {
                p.SetValue(result, p.GetValue(defaults));
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Environment config must be a JSON object.");
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var prop = props.FirstOrDefault(p => string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown config key '{entry.Name}' for {typeof(T).Name}. Valid keys: {string.Join(", ", props.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)))}.");
                prop.SetValue(result, entry.Value.Deserialize(prop.PropertyType, JsonOptions));
            }

            return result;
        }

        /// <summary>Validates a config against <typeparamref name="T"/> and returns it normalised (null when empty).</summary>
        public static string? Normalize<T>(T defaults, string? json) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            Merge(defaults, json);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject().Any() ? doc.RootElement.GetRawText() : null;
        }
    }
}
