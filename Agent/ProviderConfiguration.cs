using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveConsole.Agent;

/// <summary>One provider's setup, shared by its models. Local endpoints remain on model profiles.</summary>
public sealed class ProviderConfiguration
{
    public string ApiKey   { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string CacheTtl { get; set; } = "5m";
    public string AccountId { get; set; } = "";

    // Keep settings understood by a newer build, even when this build cannot run that provider.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public ProviderConfiguration Clone() => new()
    {
        ApiKey = ApiKey, Endpoint = Endpoint, CacheTtl = CacheTtl, AccountId = AccountId,
        Extra = Extra is null ? null : new(Extra),
    };
}
