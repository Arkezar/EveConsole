using EveConsole.Localization;

namespace EveConsole.Agent;

public enum AgentProtocol { ChatCompletions, AnthropicMessages }

/// <summary>Facts shared by setup, discovery and execution; not a list of model names.</summary>
public sealed record AgentProviderDefinition
{
    public required string Id   { get; init; }
    public required string Name { get; init; }
    public string BaseUrl { get; init; } = "";
    public string KeyUrl { get; init; } = "";
    public AgentProtocol Protocol { get; init; } = AgentProtocol.ChatCompletions;
    public bool IsLocal { get; init; }
    public bool RequiresApiKey => !IsLocal;
    public bool FilterOpenAiModels { get; init; }

    public string Label => IsLocal ? SettingsText.ModelServiceLocal
        : string.Format(SettingsText.ProviderChoicePaid, Name);
    public override string ToString() => Label;
}
