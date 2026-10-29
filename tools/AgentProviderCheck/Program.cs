using System.Text.Json;
using System.Net;
using System.Reflection;
using System.Text;
using EveConsole.Agent;
using EveConsole.Agent.Providers;
using EveConsole.Services;
using EveConsole.ViewModels;
using ReactiveUI;
using System.Reactive.Concurrency;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;

var profileDirectory = Directory.CreateTempSubdirectory("eve-provider-check-").FullName;
AppConfig.UseProfile(profileDirectory);
RxApp.MainThreadScheduler = ImmediateScheduler.Instance;
var providers = new AgentProviders([.. AgentProviders.Default.Definitions, new AgentProviderDefinition
{
    Id = "test-compatible", Name = "Test compatible", BaseUrl = "https://default.invalid/v1/",
}]);
var failures = new List<string>();
Check("a cleared provider credential is not restored from its legacy mirror", () =>
{
    var settings = Read("""
        {"OpenAiApiKey":"stale-key","ProviderConfigurations":{"openai":{"ApiKey":""}}}
        """);
    Require(settings.OpenAiApiKey == "", "The new, deliberately empty credential must win.");
});

Check("an unknown provider and its model settings survive a round trip", () =>
{
    var settings = Read("""
        {"ClaudeApiKey":"known-key","Models":[{"Id":"future-model","ProviderId":"future","Provider":0,"Model":"future-brain","FutureOption":true}],
         "ConversationRole":{"ModelId":"future-model"},
         "ProviderConfigurations":{"future":{"ApiKey":"future-key","FutureSetting":{"depth":7}}}}
        """);
    Require(AgentProviders.Default.Build(settings.Models[0], settings) is null, "Unknown providers must not execute as Claude or Local.");
    using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings));
    var model = saved.RootElement.GetProperty("Models")[0];
    Require(model.GetProperty("ProviderId").GetString() == "future", "Provider identity was lost.");
    Require(model.GetProperty("FutureOption").GetBoolean(), "Unknown model settings were lost.");
    Require(saved.RootElement.GetProperty("ProviderConfigurations").GetProperty("future").GetProperty("FutureSetting").GetProperty("depth").GetInt32() == 7,
        "Unknown provider settings were lost.");
    Require(settings.ConversationRole.ModelId == "future-model", "Unsupported model references must survive.");
});

Check("unknown legacy numbers do not borrow Local model settings", () =>
{
    var settings = Read("""{"Provider":77,"LocalModel":"local-brain","LocalEndpoint":"http://local-box:11434"}""");
    Require(settings.Models[0].ProviderId == "legacy:77" && settings.Models[0].ModelName == "", "Unknown legacy provider became a Local profile.");
    settings.Models[0].Model = "future-brain";
    settings.Models[0].Endpoint = "https://future.invalid";
    settings.MirrorLegacyModel();
    Require(settings.LocalModel == "local-brain" && settings.LocalEndpoint == "http://local-box:11434", "Unknown provider overwrote the Local mirror.");
});

Check("legacy profiles, roles and credentials migrate without changing identity", () =>
{
    var settings = Read("""
        {"ClaudeApiKey":"claude-key","ClaudeCacheTtl":"1h","OpenAiApiKey":"openai-key",
         "Models":[{"Id":"talk","Provider":1,"Model":"chat"},{"Id":"local-a","Provider":2,"Model":"brain","Endpoint":"http://a:11434"},{"Id":"local-b","Provider":2,"Model":"brain","Endpoint":"http://b:1234"}],
         "ConversationRole":{"ModelId":"talk","FallbackId":"local-a"},"AnalystRole":{"ModelId":"local-b"},"SummaryModelId":"local-a"}
        """);
    var saved = JsonSerializer.Serialize(settings);
    var again = Read(saved);
    Require(again.Models.Select(m => m.Id).SequenceEqual(new[] { "talk", "local-a", "local-b" }), "Model IDs changed.");
    Require(again.Models[0].ProviderId == "openai" && again.Models[1].ProviderId == "local", "Legacy provider mapping changed.");
    Require(again.Models[1].Endpoint == "http://a:11434" && again.Models[2].Endpoint == "http://b:1234", "Local servers were collapsed.");
    Require(again.ConversationRole.FallbackId == "local-a" && again.AnalystRole.ModelId == "local-b" && again.SummaryModelId == "local-a", "Role assignments changed.");
    Require(again.OpenAiApiKey == "openai-key" && again.ClaudeApiKey == "claude-key" && again.ClaudeCacheTtl == "1h", "Credentials or cache settings changed.");
    using var json = JsonDocument.Parse(saved);
    Require(json.RootElement.GetProperty("OpenAiApiKey").GetString() == "openai-key", "An older build cannot find its OpenAI key.");
    Require(json.RootElement.GetProperty("Models")[0].GetProperty("Provider").GetInt32() == 1, "An older build cannot find its provider number.");
    Require(JsonSerializer.Serialize(again) == saved, "Normalization is not idempotent.");
    var clone = again.Clone();
    clone.ConfigurationFor("openai").ApiKey = "edited";
    Require(again.OpenAiApiKey == "openai-key", "Editing a clone changed the original credentials.");
});

Check("pre-list files keep their chosen model and other configured providers", () =>
{
    var settings = Read("""{"Provider":1,"OpenAiApiKey":"key","OpenAiModel":"chosen","ClaudeApiKey":"other","ClaudeModel":"other-model"}""");
    Require(settings.Models[0].ProviderId == "openai" && settings.Models[0].ModelName == "chosen", "The chosen legacy model changed.");
    Require(settings.Models.Any(m => m.ProviderId == "claude" && m.ModelName == "other-model"), "Another configured provider was lost.");
    Require(settings.ConversationRole.ModelId == settings.Models[0].Id, "The chosen model did not keep the conversation role.");
});

Check("one compatible provider definition supplies setup rules and construction", () =>
{
    var settings = new AgentSettings();
    var model = new ModelProfile { ProviderId = "test-compatible", Model = "test-brain" };
    Require(providers.Find(model.ProviderId)?.RequiresApiKey == true, "The ordinary credential control is missing.");
    Require(providers.Validate(model, settings) is { Length: > 0 }, "Missing credentials should explain why setup is unavailable.");
    Require(providers.Build(model, settings) is null, "An incomplete setup must not build a provider.");
    settings.ConfigurationFor("test-compatible").ApiKey = "test-secret";
    Require(providers.Validate(model, settings) is null, "A complete setup is still unavailable.");
    var provider = providers.Build(model, settings);
    Require(provider is OpenAiCompatibleProvider { IsConfigured: true, ProviderName: "Test compatible" }, "The existing adapter did not retain the provider identity.");
});

Check("discovery and execution use the same endpoint, credential and usage identity", () =>
{
    var settings = new AgentSettings();
    settings.ConfigurationFor("test-compatible").ApiKey = "unsaved-secret";
    settings.ConfigurationFor("test-compatible").Endpoint = "https://configured.invalid/custom/v1";
    var model = new ModelProfile { ProviderId = "test-compatible", Model = "mock-brain" };
    using var handler = new CompatibleHttp();
    using var client = new HttpClient(handler);
    WithHttp(typeof(OpenAiCompatibleProvider), client, () =>
    {
        var models = providers.ListModelsAsync(model, settings).GetAwaiter().GetResult();
        Require(models.Single().Id == "mock-brain", "Discovery did not use the configured adapter.");
        var usage = new List<UsageReport>();
        async Task<string> Answer()
        {
            var answer = new StringBuilder();
            await foreach (var chunk in providers.Build(model, settings)!.StreamAsync("system", [new AgentMessage(MessageRole.User, "hi")], onUsage: usage.Add))
                answer.Append(chunk);
            return answer.ToString();
        }
        Require(Answer().GetAwaiter().GetResult() == "Hello.", "The configured provider could not answer.");
        Require(handler.Requests.Select(r => r.Url).SequenceEqual(new[] { "https://configured.invalid/custom/v1/models", "https://configured.invalid/custom/v1/chat/completions" }), "Discovery and execution disagreed about the endpoint.");
        Require(handler.Requests.All(r => r.Key == "unsaved-secret"), "Discovery and execution disagreed about the credential.");
        Require(usage.Single() is { Provider: "Test compatible", IsLocal: false, IsEstimated: false }, "Hosted usage was disguised as OpenAI, Local or an estimate.");
    });
});

Check("the same provider entry reaches Settings, saves and runtime roles", () =>
{
    var settings = Read("""
        {"Models":[{"Id":"talk","ProviderId":"test-compatible","Model":"mock-brain"},{"Id":"future","ProviderId":"future","Model":"future-brain","FutureOption":true}],
         "ConversationRole":{"ModelId":"talk"},
         "ProviderConfigurations":{"test-compatible":{"ApiKey":""},"future":{"ApiKey":" \r\nfuture-key\r\n ","FutureSetting":7}}}
        """);
    File.WriteAllText(Path.Combine(profileDirectory, "agent-settings.json"), JsonSerializer.Serialize(settings));
    var service = new AgentService(providers);
    using var roles = service.Roles;
    var vm = new AgentSettingsViewModel(service);
    try
    {
        var model = vm.SelectedModel!;
        Require(model.ServiceOptions.Any(o => o.Id == "test-compatible"), "The provider definition did not reach Settings choices.");
        Require(model.ShowApiKey && model.ShowEndpoint, "Ordinary setup controls did not follow the definition.");
        model.ApiKey = "edited-key";
        model.Endpoint = "https://configured.invalid/v1/";
        vm.FlushAsync().GetAwaiter().GetResult();
        Require(service.Roles.Conversation?.Provider?.ProviderName == "Test compatible", "Settings and runtime did not use the same registry.");
        var loaded = new AgentService(providers);
        using var reloadedRoles = loaded.Roles;
        Require(loaded.Settings.ConfigurationFor("test-compatible").ApiKey == "edited-key", "Settings dropped the new provider credential.");
        Require(loaded.Settings.ConfigurationFor("test-compatible").Endpoint == "https://configured.invalid/v1/", "Settings dropped the provider endpoint.");
        Require(loaded.Settings.Models.Single(m => m.Id == "future").Extra!.ContainsKey("FutureOption"), "Editing Settings dropped an unsupported model setting.");
        Require(loaded.Settings.ConfigurationFor("future").Extra!.ContainsKey("FutureSetting"), "Editing Settings dropped unsupported provider settings.");
        Require(loaded.Settings.ConfigurationFor("future").ApiKey == " \r\nfuture-key\r\n ", "Saving normalized an unsupported provider's credential.");
    }
    finally { vm.CloseAsync().GetAwaiter().GetResult(); }
});

Check("the Messages adapter also honors provider identity and endpoint", () =>
{
    var messages = new AgentProviders([new AgentProviderDefinition
    {
        Id = "test-messages", Name = "Test Messages", Protocol = AgentProtocol.AnthropicMessages,
        BaseUrl = "https://messages.invalid/custom/v1/",
    }]);
    var settings = new AgentSettings();
    settings.ConfigurationFor("test-messages").ApiKey = "messages-key";
    var model = new ModelProfile { ProviderId = "test-messages", Model = "mock-brain" };
    using var handler = new CompatibleHttp();
    using var client = new HttpClient(handler);
    WithHttp(typeof(ClaudeProvider), client, () =>
    {
        Require(messages.ListModelsAsync(model, settings).GetAwaiter().GetResult().Single().Id == "mock-brain", "Messages discovery failed.");
        var provider = messages.Build(model, settings)!;
        Require(provider.ProviderName == "Test Messages", "Messages usage identity was lost.");
        async Task Answer() { await foreach (var chunk in provider.StreamAsync("system", [new AgentMessage(MessageRole.User, "hi")])) { } }
        Answer().GetAwaiter().GetResult();
        Require(handler.Requests[0].Url.StartsWith("https://messages.invalid/custom/v1/models"), "Messages discovery ignored its endpoint.");
        Require(handler.Requests[1].Url == "https://messages.invalid/custom/v1/messages", "Messages execution ignored its endpoint.");
        Require(handler.Requests.All(r => r.Key == "messages-key"), "Messages credentials were lost.");
    });
});

Check("background rate discovery sees the same added provider without opening Settings", () =>
{
    using var connection = new SqliteConnection("Data Source=:memory:");
    connection.Open();
    using var services = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseSqlite(connection)).BuildServiceProvider();
    using (var scope = services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        db.ServiceRates.Add(new ServiceRate { Kind = "llm", Provider = "Test compatible", Model = "" });
        db.SaveChanges();
    }
    // A fresh local price-list fixture keeps this check offline.
    Directory.CreateDirectory(Path.Combine(profileDirectory, "price-lists"));
    File.WriteAllText(Path.Combine(profileDirectory, "price-lists", "litellm.json"), "{}");
    var scopes = services.GetRequiredService<IServiceScopeFactory>();
    var telemetry = new AgentTelemetryService(scopes, new AppErrorLogger(scopes));
    var settings = new AgentSettings();
    settings.ConfigurationFor("test-compatible").ApiKey = "background-key";
    using var handler = new CompatibleHttp();
    using var client = new HttpClient(handler);
    WithHttp(typeof(OpenAiCompatibleProvider), client, () =>
    {
        Require(ListedRates.SyncAsync(settings, telemetry, providers: providers).GetAwaiter().GetResult() == 1, "Background discovery did not add the listed model's rate row.");
        Require(handler.Requests.Single().Key == "background-key", "Background discovery did not use provider-keyed credentials.");
    });
});

Check("provider credential edits rebuild roles but unrelated saves preserve fallback", () =>
{
    var settings = Read("""
        {"Models":[{"Id":"unknown","ProviderId":"future","Model":"future-brain"},{"Id":"fallback","ProviderId":"test-compatible","Model":"mock-brain"}],
         "ConversationRole":{"ModelId":"unknown","FallbackId":"fallback"},"ProviderConfigurations":{"test-compatible":{"ApiKey":"first-key"}}}
        """);
    using var roles = new ModelRoles();
    roles.Configure(settings, m => providers.Build(m, settings));
    var seat = roles.Conversation!;
    Require(seat.Provider is null && roles.FailOver(seat) is not null && seat.IsOnFallback, "An unknown provider could not use its explicit fallback.");
    var edited = settings.Clone();
    edited.AgentName = "Another name";
    edited.ConfigurationFor("unrelated-provider").ApiKey = "unrelated-key";
    roles.Configure(edited, m => providers.Build(m, edited));
    Require(ReferenceEquals(seat, roles.Conversation) && seat.IsOnFallback, "An unrelated save reset active fallback.");
    edited.ConfigurationFor("test-compatible").ApiKey = "replacement-key";
    roles.Configure(edited, m => providers.Build(m, edited));
    Require(!ReferenceEquals(seat, roles.Conversation), "The changed provider credential left a stale runtime adapter.");
});

Check("invalid endpoints explain setup failure without echoing secrets", () =>
{
    var settings = new AgentSettings();
    var configuration = settings.ConfigurationFor("test-compatible");
    configuration.ApiKey = "private-key";
    configuration.Endpoint = "https://user:private-key@provider.invalid/v1/";
    var model = new ModelProfile { ProviderId = "test-compatible", Model = "mock-brain" };
    var why = providers.Validate(model, settings);
    Require(why is { Length: > 0 } && !why.Contains("private-key") && providers.Build(model, settings) is null, "Unsafe endpoint data escaped validation.");
});

Check("null provider fields cannot crash migration or restore stale credentials", () =>
{
    var settings = Read("""{"OpenAiApiKey":"stale","ProviderConfigurations":{"openai":{"ApiKey":null,"Endpoint":null,"CacheTtl":null},"future":null}}""");
    Require(settings.OpenAiApiKey == "", "A null authoritative credential should be empty, not restored from a mirror.");
    var copy = settings.Clone();
    Require(copy.ConfigurationFor("future") is not null, "An empty unsupported configuration could not be preserved.");
});

Check("Try model uses typed provider settings and never displays a refused credential", () =>
{
    var settings = Read("""{"Models":[{"Id":"talk","ProviderId":"test-compatible","Model":"mock-brain"}],"ConversationRole":{"ModelId":"talk"}}""");
    File.WriteAllText(Path.Combine(profileDirectory, "agent-settings.json"), JsonSerializer.Serialize(settings));
    var service = new AgentService(providers);
    using var roles = service.Roles;
    var vm = new AgentSettingsViewModel(service);
    using var handler = new CompatibleHttp();
    using var client = new HttpClient(handler);
    WithHttp(typeof(OpenAiCompatibleProvider), client, () =>
    {
        try
        {
            var model = vm.SelectedModel!;
            model.ApiKey = "try-secret";
            model.Endpoint = "https://typed.invalid/v1/";
            Require(vm.TestModelAsync(model).GetAwaiter().GetResult().Ok, "Try model did not use the added definition.");
            Require(handler.Requests.Last() == ("https://typed.invalid/v1/chat/completions", "try-secret"), "Try model ignored a typed credential or endpoint.");
            model.ModelList.LoadAsync(force: true).GetAwaiter().GetResult();
            Require(model.ModelList.Status == "1 model from Test compatible.", "A single discovered model used the plural label.");
            handler.RefusedKey = "try-secret";
            var refused = vm.TestModelAsync(model).GetAwaiter().GetResult();
            Require(!refused.Ok && !refused.Message.Contains("try-secret"), "A provider refusal exposed the credential.");
        }
        finally
        {
            vm.CloseAsync().GetAwaiter().GetResult();
            vm.CloseAsync().GetAwaiter().GetResult();
        }
    });
});

Check("OpenAI model and speech controls still edit one credential", () =>
{
    var settings = Read("""{"Models":[{"Id":"talk","Provider":1,"Model":"chat"}],"ConversationRole":{"ModelId":"talk"}}""");
    File.WriteAllText(Path.Combine(profileDirectory, "agent-settings.json"), JsonSerializer.Serialize(settings));
    var service = new AgentService();
    using var roles = service.Roles;
    var vm = new AgentSettingsViewModel(service);
    try
    {
        vm.SelectedModel!.ApiKey = "from-model";
        Require(vm.OpenAiApiKey == "from-model", "The speech control did not follow the model credential.");
        vm.OpenAiApiKey = "from-speech";
        Require(vm.SelectedModel.ApiKey == "from-speech", "The model control did not follow the speech credential.");
        vm.OpenAiApiKey = " \r\nfrom-speech\r\n ";
        vm.FlushAsync().GetAwaiter().GetResult();
        Require(service.Settings.OpenAiApiKey == "from-speech" && service.Settings.ConfigurationFor("openai").ApiKey == "from-speech", "Runtime speech readers did not receive the shared credential.");
    }
    finally { vm.CloseAsync().GetAwaiter().GetResult(); }
});

Check("authoritative credentials win even when legacy properties appear last", () =>
{
    var settings = Read("""{"ProviderConfigurations":{"openai":{"ApiKey":"new-key"},"claude":{"ApiKey":"new-claude","CacheTtl":"1h"}},"OpenAiApiKey":"stale-key","ClaudeApiKey":"stale-claude","ClaudeCacheTtl":"5m"}""");
    Require(settings.OpenAiApiKey == "new-key" && settings.ClaudeApiKey == "new-claude" && settings.ClaudeCacheTtl == "1h", "Property order changed migration precedence.");
});

Check("the Settings ComboBox can switch from Claude or an unavailable provider and persist every choice", () =>
{
    AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
    foreach (var initialId in new[] { "claude", "future-ui-provider" })
    {
        var settings = Read("""{"Models":[{"Id":"talk","ProviderId":"claude","Model":"chosen"}],"ConversationRole":{"ModelId":"talk"}}""");
        settings.Models[0].ProviderId = initialId;
        File.WriteAllText(Path.Combine(profileDirectory, "agent-settings.json"), JsonSerializer.Serialize(settings));
        var service = new AgentService();
        using var roles = service.Roles;
        var vm = new AgentSettingsViewModel(service);
        using var handler = new CompatibleHttp();
        using var client = new HttpClient(handler);
        WithHttp(typeof(OpenAiCompatibleProvider), client, () =>
        {
            try
            {
                var model = vm.SelectedModel!;
                var combo = new ComboBox { DataContext = model };
                using var items = combo.Bind(ComboBox.ItemsSourceProperty, new Binding(nameof(model.ServiceOptions)));
                using var selected = combo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(model.SelectedService)));
                Dispatcher.UIThread.RunJobs();
                Require((combo.SelectedItem as ModelProfileVm.ServiceOption)?.Id == initialId, "The control did not display the initial provider.");
                foreach (var id in AgentProviders.Default.Definitions.Select(d => d.Id))
                {
                    combo.SelectedItem = combo.Items.OfType<ModelProfileVm.ServiceOption>().Single(o => o.Id == id);
                    Dispatcher.UIThread.RunJobs();
                    Require(model.ProviderId == id && (combo.SelectedItem as ModelProfileVm.ServiceOption)?.Id == id,
                        $"Selecting {id} reverted to {model.ProviderId} / {(combo.SelectedItem as ModelProfileVm.ServiceOption)?.Id}.");
                    vm.FlushAsync().GetAwaiter().GetResult();
                    Require(service.Settings.Models.Single().ProviderId == id, $"Selecting {id} was not persisted.");
                }
            }
            finally { vm.CloseAsync().GetAwaiter().GetResult(); }
        });
    }
});

Directory.Delete(profileDirectory, recursive: true);
if (failures.Count == 0) Console.WriteLine("Agent provider check passed.");
else foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

void Check(string name, Action test)
{
    try { test(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failures.Add("FAIL: " + name + ": " + ex.Message); }
}

static AgentSettings Read(string json)
{
    var settings = JsonSerializer.Deserialize<AgentSettings>(json)!;
    settings.NormalizeModels();
    return settings;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// Same internal HTTP seam as AgentStreamCheck, restored even when an assertion fails.
static void WithHttp(Type provider, HttpClient client, Action scenario)
{
    var field = provider.GetField("_http", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{provider.Name}._http not found — the HTTP seam has moved.");
    var original = field.GetValue(null);
    field.SetValue(null, client);
    try { scenario(); }
    finally { field.SetValue(null, original); }
}

sealed class CompatibleHttp : HttpMessageHandler
{
    public List<(string Url, string? Key)> Requests { get; } = [];
    public string? RefusedKey { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter
            ?? (request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null)));
        if (RefusedKey is not null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Refused credential: " + RefusedKey) });
        var content = request.Method == HttpMethod.Get
            ? """{"data":[{"id":"mock-brain"}]}"""
            : request.RequestUri.AbsolutePath.EndsWith("/messages")
            ? "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello.\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
            : "data: {\"choices\":[{\"delta\":{\"content\":\"Hello.\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\ndata: [DONE]\n\n";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
    }
}
