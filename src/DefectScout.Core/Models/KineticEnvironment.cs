using System.Text.Json.Serialization;

namespace DefectScout.Core.Models;

public class KineticEnvironment
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("webUrl")]
    public string WebUrl { get; set; } = string.Empty;

    [JsonPropertyName("restApiBaseUrl")]
    public string RestApiBaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("company")]
    public string Company { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    /// <summary>Returns the version slug used in directory names (e.g. "2026.1" → "2026-1").</summary>
    [JsonIgnore]
    public string VersionSlug => Version.Replace('.', '-');

    /// <summary>Returns a safe env slug for file names (lowercase, spaces → underscores).</summary>
    [JsonIgnore]
    public string EnvSlug => Name.ToLowerInvariant().Replace(' ', '_');
}
