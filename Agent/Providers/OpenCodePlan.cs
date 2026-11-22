using System.Text.Json;

namespace EveConsole.Agent.Providers;

/// <summary>Each OpenCode gateway uses the key for its own plan to list models and bill requests.</summary>
public sealed class OpenCodePlan
{
    private const string Gateway = "https://opencode.ai/inference";
    public static OpenCodePlan Zen { get; } = new("OpenCode Zen", Gateway);
    public static OpenCodePlan Go  { get; } = new("OpenCode Go", Gateway + "/go");

    public string Name { get; }
    internal string ChatBase  { get; }
    internal string ModelsUrl { get; }

    private OpenCodePlan(string name, string gateway)
    {
        Name      = name;
        ChatBase  = gateway + "/openai/v1/";
        ModelsUrl = gateway + "/v1/models";
    }

    public static OpenCodePlan? For(AgentProviderType provider) => provider switch
    {
        AgentProviderType.OpenCodeZen => Zen,
        AgentProviderType.OpenCodeGo  => Go,
        _ => null,
    };

    internal string SelectKey(string? zenKey, string? goKey) => (this == Go ? goKey : zenKey)?.Trim() ?? "";

    internal bool OffersChat(JsonElement model)
    {
        // If metadata is unavailable the gateway still returns a standard OpenAI list of IDs.
        if (model.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "chat")
            return false;
        // Zen's free-tier chat models accept only the OpenCode client, even with a Console key.
        return this != Zen || !IsFreeTier(model);
    }

    private static bool IsFreeTier(JsonElement model) =>
        model.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object
        && cost.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object
        && ZeroPrice(text, "input") && ZeroPrice(text, "output");

    private static bool ZeroPrice(JsonElement text, string direction) =>
        text.TryGetProperty(direction, out var unit) && unit.ValueKind == JsonValueKind.Object
        && unit.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Number && price.GetDouble() == 0;
}
