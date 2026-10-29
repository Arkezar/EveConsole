using EveConsole.Agent.Providers;
using EveConsole.Localization;

namespace EveConsole.Agent;

/// <summary>One configuration seam for provider setup, discovery and execution.
/// Add a provider using an existing protocol in Default; the numeric enum is legacy-only.</summary>
public sealed class AgentProviders
{
    public const string Claude = "claude";
    public const string OpenAi = "openai";
    public const string Local  = "local";

    public static AgentProviders Default { get; } = new([
        new() { Id = Local, Name = "Local", IsLocal = true },
        new() { Id = Claude, Name = ClaudeProvider.ServiceName, Protocol = AgentProtocol.AnthropicMessages,
                BaseUrl = "https://api.anthropic.com/v1/", KeyUrl = "https://console.anthropic.com/settings/keys" },
        new() { Id = OpenAi, Name = OpenAiCompatibleProvider.OpenAiName, BaseUrl = "https://api.openai.com/v1/",
                FilterOpenAiModels = true, KeyUrl = "https://platform.openai.com/api-keys" },
    ]);

    public AgentProviders(IEnumerable<AgentProviderDefinition> definitions) => Definitions = definitions.ToArray();

    public IReadOnlyList<AgentProviderDefinition> Definitions { get; }
    public AgentProviderDefinition? Find(string id) => Definitions.FirstOrDefault(d => d.Id == id);

    public string Describe(ModelProfile model)
    {
        var definition = Find(model.ProviderId);
        if (definition is null) return string.Format(SettingsText.ProviderUnsupported, model.ProviderId);
        var name = model.ModelName.Length > 0 ? model.ModelName : SettingsText.ModelNoneChosen;
        return definition.IsLocal
            ? Uri.TryCreate((model.Endpoint ?? "").Trim(), UriKind.Absolute, out var uri)
                ? string.Format(SettingsText.ModelDescribeLocalOn, name, uri.Host)
                : string.Format(SettingsText.ModelDescribeLocal, name)
            : $"{definition.Name} — {name}";
    }

    public string Endpoint(ModelProfile model, AgentSettings settings)
    {
        var definition = Find(model.ProviderId);
        if (definition is null) return "";
        if (definition.IsLocal) return (model.Endpoint ?? "").Trim();
        var endpoint = settings.ConfigurationFor(model.ProviderId).Endpoint.Trim();
        return endpoint.Length > 0 ? endpoint : definition.BaseUrl;
    }

    /// <summary>The same requirements apply to listing and execution; only execution needs a model.</summary>
    public string? Validate(ModelProfile model, AgentSettings settings, bool requireModel = true)
    {
        var definition = Find(model.ProviderId);
        if (definition is null) return string.Format(SettingsText.ProviderUnsupported, model.ProviderId);
        if (requireModel && model.ModelName.Length == 0) return SettingsText.AgentTestChooseModel;
        if (definition.RequiresApiKey && string.IsNullOrWhiteSpace(settings.ConfigurationFor(model.ProviderId).ApiKey))
            return string.Format(SettingsText.ProviderNeedKey, definition.Name);
        var endpoint = Endpoint(model, settings);
        if (endpoint.Length == 0) return SettingsText.ModelNeedServerAddress;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return SettingsText.ProviderInvalidEndpoint;
        return null;
    }

    public IAgentProvider? Build(ModelProfile model, AgentSettings settings)
    {
        if (Validate(model, settings) is not null) return null;
        var definition = Find(model.ProviderId)!;
        var configuration = settings.ConfigurationFor(model.ProviderId);
        return definition.Protocol switch
        {
            AgentProtocol.AnthropicMessages => new ClaudeProvider(configuration.ApiKey.Trim(), model.ModelName, configuration.CacheTtl, Endpoint(model, settings), definition.Name),
            _ when definition.IsLocal => OpenAiCompatibleProvider.Local(model.Endpoint, model.ModelName, model.Think),
            _ => OpenAiCompatibleProvider.Compatible(definition.Name, Endpoint(model, settings), configuration.ApiKey.Trim(), model.ModelName),
        };
    }

    public Task<IReadOnlyList<ModelListing>> ListModelsAsync(ModelProfile model, AgentSettings settings, CancellationToken ct = default)
    {
        if (Validate(model, settings, requireModel: false) is { } why)
            throw new InvalidOperationException(why);
        var definition = Find(model.ProviderId)!;
        var key = settings.ConfigurationFor(model.ProviderId).ApiKey.Trim();
        return definition.Protocol switch
        {
            AgentProtocol.AnthropicMessages => ClaudeProvider.ListModelsAsync(key, ct, Endpoint(model, settings), definition.Name),
            _ when definition.IsLocal => OpenAiCompatibleProvider.ListLocalModelsAsync(model.Endpoint, ct),
            _ => OpenAiCompatibleProvider.ListCompatibleModelsAsync(Endpoint(model, settings), key, definition.Name, definition.FilterOpenAiModels, ct),
        };
    }

    // Numeric identities below are only for compatibility with older settings files.
    public static string LegacyId(AgentProviderType provider) => provider switch
    {
        AgentProviderType.Claude => Claude,
        AgentProviderType.OpenAI => OpenAi,
        AgentProviderType.Local  => Local,
        _ => $"legacy:{(int)provider}",
    };

    public static AgentProviderType? LegacyType(string id) => id switch
    {
        Claude => AgentProviderType.Claude,
        OpenAi => AgentProviderType.OpenAI,
        Local  => AgentProviderType.Local,
        _ => null,
    };
}
