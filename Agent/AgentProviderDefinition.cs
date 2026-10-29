using EveConsole.Localization;

namespace EveConsole.Agent;

public enum AgentProtocol { ChatCompletions, AnthropicMessages, CodexResponses }

/// <summary>Facts shared by setup, discovery and execution; not a list of model names.</summary>
public sealed record AgentProviderDefinition
{
    public required string Id   { get; init; }
    public required string Name { get; init; }
    public string BaseUrl { get; init; } = "";
    public string KeyUrl { get; init; } = "";
    public string ModelsPath { get; init; } = "models";
    /// <summary>Optional Chat Completions header for stable conversation routing.</summary>
    public string SessionHeader { get; init; } = "";
    public AgentProtocol Protocol { get; init; } = AgentProtocol.ChatCompletions;
    public bool IsLocal { get; init; }
    public bool UsesChatGpt => Protocol == AgentProtocol.CodexResponses;
    public bool RequiresApiKey => !IsLocal && !UsesChatGpt;
    public bool FilterOpenAiModels { get; init; }

    public string Label => IsLocal ? SettingsText.ModelServiceLocal
        : UsesChatGpt ? SettingsText.CodexProviderChoice
        : string.Format(SettingsText.ProviderChoicePaid, Name);
    public override string ToString() => Label;
}
