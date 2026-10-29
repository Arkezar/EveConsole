using EveConsole.Agent.Providers;
using EveConsole.Services;

namespace EveConsole.Agent;

/// <summary>
/// Which service's list goes with which rate: what usage is recorded — and so priced — under,
/// for each list a model can be chosen from. And asking every service there is a key for, so a
/// model a service has added gets its rate row without anyone opening Settings.
///
/// <para>⚠️ The provider names are the ones usage is recorded under, not new ones: a rate row
/// under any other name would never be found (see ServiceRate).</para>
/// </summary>
public static class ListedRates
{
    public static (string Kind, string Provider) OpenAiSpeech    => ("tts", nameof(TtsProvider.OpenAi));
    public static (string Kind, string Provider) ElevenLabs      => ("tts", nameof(TtsProvider.ElevenLabs));
    public static (string Kind, string Provider) OpenAiTranscribe => ("stt", nameof(SpeechInputProvider.OpenAiWhisper));

    /// <summary>
    /// Every paid service with a key: its list, and a rate row for each model on it that has none;
    /// then each row still holding a copied rate given the model's published one, where LiteLLM's
    /// list has it (see PublishedRates). Returns how many rows were added or priced. A service that
    /// does not answer is passed over — this only adds, and the next time will do.
    /// </summary>
    public static async Task<int> SyncAsync(AgentSettings keys, AgentTelemetryService telemetry,
        CancellationToken ct = default, AgentProviders? providers = null)
    {
        providers ??= AgentProviders.Default;
        var added = 0;
        async Task Add((string Kind, string Provider) rate, Func<Task<IReadOnlyList<ModelListing>>> list)
        {
            try { added += await telemetry.AddListedRatesAsync(rate.Kind, rate.Provider, (await list()).Select(m => m.Id)); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* not answering: next time */ }
        }

        foreach (var definition in providers.Definitions.Where(d => !d.IsLocal))
        {
            var model = new ModelProfile { ProviderId = definition.Id };
            if (providers.Validate(model, keys, requireModel: false) is null)
                await Add(("llm", definition.Name), () => providers.ListModelsAsync(model, keys, ct));
        }

        var openAi = keys.OpenAiApiKey.Trim();
        if (openAi.Length > 0)
        {
            await Add(OpenAiSpeech,     () => OpenAiCompatibleProvider.ListOpenAiSpeechModelsAsync(openAi, ct));
            await Add(OpenAiTranscribe, () => OpenAiCompatibleProvider.ListOpenAiTranscriptionModelsAsync(openAi, ct));
        }

        var eleven = keys.ElevenLabsApiKey.Trim();
        if (eleven.Length > 0)
            await Add(ElevenLabs, () => ElevenLabsTtsService.ListModelsAsync(eleven, ct));

        return added + await telemetry.ApplyPublishedRatesAsync(ct);
    }
}
