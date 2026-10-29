using System.Net;
using System.Reflection;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using EveConsole.Agent;
using EveConsole.Agent.Providers;
using EveConsole.Agent.Tools;
using EveConsole.Services;
using EveConsole.ViewModels;
using ReactiveUI;

var directory = Directory.CreateTempSubdirectory("eve-codex-check-").FullName;
AppConfig.UseProfile(directory);
RxApp.MainThreadScheduler = ImmediateScheduler.Instance;
var secretTool = typeof(SecretStore).GetField("_hasSecretTool", BindingFlags.Static | BindingFlags.NonPublic)!;
var originalSecretTool = secretTool.GetValue(null);
var securityTool = typeof(SecretStore).GetField("_hasSecurityTool", BindingFlags.Static | BindingFlags.NonPublic)!;
var originalSecurityTool = securityTool.GetValue(null);
securityTool.SetValue(null, false);
secretTool.SetValue(null, false); // Never put test credentials into the operator's desktop keyring.
using var auth = new FakeAuth();
using var inference = new FakeCodex();
using var authClient = new HttpClient(auth);
using var inferenceClient = new HttpClient(inference);
using var authSeam = new HttpSeam(typeof(CodexAccount), authClient);
using var inferenceSeam = new HttpSeam(typeof(CodexProvider), inferenceClient);
var failures = new List<string>();
var settings = new AgentSettings { OpenAiApiKey = "speech-key" };
var profile = new ModelProfile { ProviderId = AgentProviders.Codex, Model = "test-codex" };
var analyst = new ModelProfile { ProviderId = AgentProviders.Codex, Model = "test-codex" };
settings.Models = [profile, analyst];
settings.ConversationRole.ModelId = profile.Id;
settings.AnalystRole.ModelId = analyst.Id;
try
{
    await Check("Codex requires subscription sign-in, not the OpenAI API key", async () =>
    {
        var definition = AgentProviders.Default.Find(AgentProviders.Codex)!;
        Require(definition.UsesChatGpt && !definition.RequiresApiKey, "Codex still asks for an API key.");
        Require(AgentProviders.Default.Build(profile, settings) is null, "API credentials authorized a subscription provider.");
        settings.ConfigurationFor(AgentProviders.Codex).Endpoint = "https://untrusted.invalid/";
        Require(AgentProviders.Default.Endpoint(profile, settings) == CodexProvider.BaseUrl, "A custom endpoint could receive subscription credentials.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => CodexAccount.BeginAsync(cancelled.Token));
    });

    await Check("device-code login saves private, profile-local tokens and account identity", async () =>
    {
        var device = await CodexAccount.BeginAsync(default);
        Require(device.Code == "TEST-CODE" && device.Interval == 1, "The device-code reply was not parsed.");
        var id = await CodexAccount.CompleteAsync(device, default);
        settings.ConfigurationFor(AgentProviders.Codex).AccountId = id;
        Require(id == "test-account" && CodexAccount.IsSignedIn(id), "Login did not persist.");
        Require(auth.Polls >= 2, "A pending authorization was not polled again.");
        var path = Path.Combine(directory, "codex-account.json");
        if (!OperatingSystem.IsWindows())
            Require((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0, "Credentials are readable by another user.");
        var saved = JsonSerializer.Serialize(settings);
        Require(!saved.Contains("refresh-secret") && !saved.Contains(auth.LastAccessToken), "Tokens leaked into agent settings.");
        var clone = settings.Clone();
        clone.ConfigurationFor(AgentProviders.Codex).AccountId = "changed";
        Require(settings.ConfigurationFor(AgentProviders.Codex).AccountId == id, "Cloning shares mutable account setup.");
    });

    await Check("expired tokens refresh once across concurrent calls and keep the rotated refresh token", async () =>
    {
        var tokens = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CodexAccount.GetAsync("test-account", default)));
        Require(auth.Refreshes == 1 && tokens.All(t => t.RefreshToken == "rotated-secret-1"), "Concurrent refresh reused a stale refresh token.");
        await CodexAccount.GetAsync("test-account", default, refresh: true);
        Require(auth.Refreshes == 2 && auth.LastRefresh == "rotated-secret-1", "Rotated credentials were not persisted.");
    });

    await Check("discovery advertises a catalog version that includes current subscription models", async () =>
    {
        inference.MinimumCatalogVersion = new Version(0, 144, 0);
        try
        {
            var models = await AgentProviders.Default.ListModelsAsync(profile, settings);
            Require(models.Any(m => m.Id == "test-codex"), "The obsolete client version returns only hidden models, leaving the signed-in dropdown empty.");
        }
        finally { inference.MinimumCatalogVersion = new Version(0, 0, 0); }
    });

    await Check("subscription discovery, availability and streaming use Codex identity and no API prices", async () =>
    {
        var models = await AgentProviders.Default.ListModelsAsync(profile, settings);
        Require(models.Count == 1 && models[0] == new ModelListing("test-codex", "Test Codex"), "Codex model discovery failed or included a hidden model.");
        var provider = AgentProviders.Default.Build(profile, settings)!;
        Require(provider is CodexProvider && provider.IsConfigured && await provider.IsAvailableAsync(), "Codex did not reach runtime roles.");
        var usage = new List<UsageReport>();
        var answer = await Answer(provider, onUsage: usage.Add);
        Require(answer == "Hello.", "Responses text did not stream.");
        Require(usage.Single() is { Provider: CodexProvider.ServiceName, IsLocal: false, IsEstimated: false, InputTokens: 75, CacheReadTokens: 25, OutputTokens: 4 }, "Usage or cache accounting borrowed API identity.");
        using var prices = JsonDocument.Parse("{}");
        Require(PublishedRates.Find(prices, "llm", CodexProvider.ServiceName, "test-codex") is null, "API token rates were assigned to a subscription.");
        Require(inference.Sessions.Last() == "conversation-1", "Conversation routing was lost.");
    });

    await Check("tool continuation retains reasoning, function output and images", async () =>
    {
        inference.Mode = "tools";
        inference.Bodies.Clear();
        var tool = new PictureTool();
        var usage = new List<UsageReport>();
        var answer = await Answer(new CodexProvider("test-account", "test-codex"), [tool], usage.Add);
        Require(answer == "Hello." && tool.Calls == 1 && usage.Count == 2, "Tool round did not complete exactly once.");
        using var continuation = JsonDocument.Parse(inference.Bodies.Last());
        var input = continuation.RootElement.GetProperty("input").EnumerateArray().ToList();
        Require(input.Any(i => i.TryGetProperty("type", out var t) && t.GetString() == "reasoning" && i.GetProperty("encrypted_content").GetString() == "opaque"), "Encrypted reasoning was dropped.");
        Require(input.Any(i => i.TryGetProperty("type", out var t) && t.GetString() == "function_call_output" && i.GetProperty("call_id").GetString() == "call-1" && i.GetProperty("output").GetString() == "tool result"), "Tool output has the wrong call identity.");
        Require(inference.Bodies.Last().Contains("input_image") && inference.Bodies.Last().Contains("data:image/png;base64,"), "The tool image was discarded.");
        Require(inference.Bodies.All(b => !b.Contains("chat/completions") && !b.Contains("max_completion_tokens")), "Chat Completions fields leaked into Responses.");
        Require(inference.Sessions.TakeLast(2).Distinct().Count() == 1, "Tool rounds changed conversation identity.");
    });

    await Check("streaming continuations stay off the caller's UI context", async () =>
    {
        inference.Mode = "text";
        var context = new CountingContext();
        SynchronizationContext.SetSynchronizationContext(context);
        Task<string> answer;
        try { answer = Answer(new CodexProvider("test-account", "test-codex")); }
        finally { SynchronizationContext.SetSynchronizationContext(null); }
        await answer;
        Require(context.Posts == 0, "Provider resumed on the UI context.");
    });

    await Check("unauthorized requests refresh once and keep errors free of credentials", async () =>
    {
        var before = auth.Refreshes;
        inference.Refusals = 1;
        await Answer(new CodexProvider("test-account", "test-codex"));
        Require(auth.Refreshes == before + 1, "A 401 did not refresh and retry.");
        inference.Refusals = 2;
        var error = await Throws<HttpRequestException>(() => Answer(new CodexProvider("test-account", "test-codex")));
        Require(error.StatusCode == HttpStatusCode.Unauthorized && !error.Message.Contains("secret"), "A refusal lost its status or exposed credentials.");
        inference.Refusals = 0;
        auth.RefuseRefresh = true;
        var authError = await Throws<HttpRequestException>(() => CodexAccount.GetAsync("test-account", default, refresh: true));
        Require(!authError.Message.Contains("secret"), "An authentication error exposed the response body.");
        auth.RefuseRefresh = false;
    });

    await Check("truncated, failed and incomplete streams cannot execute tools", async () =>
    {
        var provider = new CodexProvider("test-account", "test-codex");
        inference.Mode = "truncated";
        await Throws<IOException>(() => Answer(provider));
        inference.Mode = "failed";
        await Throws<InvalidOperationException>(() => Answer(provider));
        inference.Mode = "incomplete";
        var tool = new PictureTool();
        var answer = await Answer(provider, [tool]);
        Require(answer.Contains("ran out of room") && tool.Calls == 0, "Incomplete tool calls were executed.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => Answer(provider, ct: cancelled.Token));
    });

    await Check("tool budgets bound side effects and allow a final wrap-up round", async () =>
    {
        inference.Mode = "budget";
        inference.Bodies.Clear();
        var tool = new PictureTool();
        await Answer(new CodexProvider("test-account", "test-codex"), [tool]);
        Require(tool.Calls == 20 && inference.Bodies.Count == 22, "Tool budget changed or a wrap-up round was skipped.");
        using var wrap = JsonDocument.Parse(inference.Bodies.Last());
        Require(!wrap.RootElement.TryGetProperty("tools", out _), "The wrap-up still offers tools.");
    });

    await Check("a copied profile can sign into the same account and rebuild an initially missing provider", async () =>
    {
        inference.Mode = "text";
        await CodexAccount.SignOutAsync();
        File.WriteAllText(Path.Combine(directory, "agent-settings.json"), JsonSerializer.Serialize(settings));
        var service = new AgentService();
        using var roles = service.Roles;
        Require(service.Roles.Conversation?.Provider is null, "The copied profile unexpectedly has usable credentials.");
        var vm = new AgentSettingsViewModel(service);
        try
        {
            auth.InitiallyExpired = false;
            await ((ReactiveCommand<Unit, Unit>)vm.CodexSignInCommand).Execute().ToTask();
            Require(vm.CodexSignedIn && service.Roles.Conversation?.Provider is CodexProvider, "Same-account login did not rebuild the initially missing provider.");
        }
        finally { await vm.CloseAsync(); }
    });

    await Check("Settings sign-out/sign-in updates every Codex model and runtime without changing speech", async () =>
    {
        inference.Mode = "text";
        File.WriteAllText(Path.Combine(directory, "agent-settings.json"), JsonSerializer.Serialize(settings));
        var service = new AgentService();
        using var roles = service.Roles;
        var vm = new AgentSettingsViewModel(service);
        try
        {
            var model = vm.SelectedModel!;
            Require(model.ServiceOptions.Any(o => o.Id == AgentProviders.Codex) && model.ShowCodex && !model.ShowApiKey && !model.ShowEndpoint, "Settings lacks subscription controls.");
            await ((ReactiveCommand<Unit, Unit>)vm.CodexSignOutCommand).Execute().ToTask();
            Require(!vm.CodexSignedIn && !CodexAccount.IsSignedIn("test-account") && service.Roles.Conversation?.Provider is null && service.Roles.Analyst?.Provider is null, "Sign-out left an active provider.");
            Require(!File.Exists(Path.Combine(directory, "codex-account.json")), "Sign-out left the token file.");
            auth.InitiallyExpired = false;
            await ((ReactiveCommand<Unit, Unit>)vm.CodexSignInCommand).Execute().ToTask();
            Require(vm.CodexSignedIn && !vm.CodexSigningIn && vm.CodexCode == "", "Login did not reach Settings or clear the code.");
            Require(service.Roles.Conversation?.Provider is CodexProvider && service.Roles.Analyst?.Provider is CodexProvider, "Login did not rebuild both runtime roles.");
            Require(service.Settings.OpenAiApiKey == "speech-key", "Subscription login replaced the speech key.");
            await model.ModelList.LoadAsync(force: true);
            Require(model.ModelList.Listings.Any(m => m.Id == "test-codex"), "Settings did not load subscription models.");
            await ((ReactiveCommand<Unit, Unit>)vm.CodexSignOutCommand).Execute().ToTask();
            var login = ((ReactiveCommand<Unit, Unit>)vm.CodexSignInCommand).Execute().ToTask();
            ((ReactiveCommand<Unit, Unit>)vm.CodexCancelCommand).Execute().Subscribe();
            await login;
            Require(!vm.CodexSignedIn && !vm.CodexSigningIn, "Cancelled login restored credentials.");
        }
        finally { await vm.CloseAsync(); }
    });
}
finally
{
    secretTool.SetValue(null, originalSecretTool);
    securityTool.SetValue(null, originalSecurityTool);
    Directory.Delete(directory, recursive: true);
}
foreach (var failure in failures) Console.Error.WriteLine(failure);
Console.WriteLine(failures.Count == 0 ? "Codex provider check passed." : "Codex provider check FAILED.");
return failures.Count == 0 ? 0 : 1;

async Task Check(string name, Func<Task> scenario)
{
    try { await scenario(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failures.Add("FAIL: " + name + ": " + ex.Message); }
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T ex) { return ex; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static async Task<string> Answer(IAgentProvider provider, IReadOnlyList<IAgentTool>? tools = null, Action<UsageReport>? onUsage = null, CancellationToken ct = default)
{
    var answer = new StringBuilder();
    await foreach (var piece in provider.StreamAsync("stable instructions", [new AgentMessage(MessageRole.User, "hi")], tools, onUsage,
        volatileContext: "live app context", sessionId: "conversation-1", ct: ct).ConfigureAwait(false)) answer.Append(piece);
    return answer.ToString();
}
sealed class HttpSeam : IDisposable
{
    private readonly FieldInfo _field;
    private readonly object? _original;
    public HttpSeam(Type type, HttpClient client)
    {
        _field = type.GetField("_http", BindingFlags.NonPublic | BindingFlags.Static)!;
        _original = _field.GetValue(null);
        _field.SetValue(null, client);
    }
    public void Dispose() => _field.SetValue(null, _original);
}
sealed class CountingContext : SynchronizationContext
{
    public int Posts;
    public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref Posts); ThreadPool.QueueUserWorkItem(_ => d(state)); }
}
sealed class FakeAuth : HttpMessageHandler
{
    public int Polls, Refreshes;
    public bool InitiallyExpired = true, RefuseRefresh;
    public string LastRefresh = "", LastAccessToken = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(5, ct).ConfigureAwait(false);
        if (request.RequestUri!.Host != "auth.openai.com") throw new InvalidOperationException("Authentication left OpenAI.");
        var path = request.RequestUri.AbsolutePath;
        if (path.EndsWith("/usercode")) return Json("{\"device_auth_id\":\"device-1\",\"user_code\":\"TEST-CODE\",\"interval\":\"0\"}");
        if (path.EndsWith("/deviceauth/token"))
            return ++Polls % 2 == 1 ? new(HttpStatusCode.Forbidden) : Json("{\"authorization_code\":\"code-secret\",\"code_verifier\":\"verifier-secret\"}");
        var body = await request.Content!.ReadAsStringAsync(ct);
        var refresh = body.Contains("grant_type=refresh_token");
        if (refresh && RefuseRefresh) return new(HttpStatusCode.Unauthorized) { Content = new StringContent("refresh-secret") };
        if (refresh)
        {
            LastRefresh = Uri.UnescapeDataString(body.Split('&').Single(p => p.StartsWith("refresh_token="))[14..]);
            Refreshes++;
        }
        else if (!body.Contains("verifier-secret") || !body.Contains("deviceauth%2Fcallback")) throw new InvalidOperationException("Missing PKCE verifier or redirect URI.");
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(!refresh && InitiallyExpired ? -1 : 1).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = "test-account" },
        });
        LastAccessToken = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        return Json(JsonSerializer.Serialize(new { access_token = LastAccessToken, refresh_token = refresh ? "rotated-secret-" + Refreshes : "refresh-secret" }));
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
sealed class FakeCodex : HttpMessageHandler
{
    public string Mode = "text";
    public Version MinimumCatalogVersion = new(0, 0, 0);
    public int Refusals;
    public List<string> Bodies { get; } = [];
    public List<string> Sessions { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(5, ct).ConfigureAwait(false);
        if (request.RequestUri!.Host != "chatgpt.com" || !request.RequestUri.AbsolutePath.StartsWith("/backend-api/codex/")) throw new InvalidOperationException("Subscription credentials left Codex.");
        if (request.Headers.GetValues("chatgpt-account-id").Single() != "test-account" || request.Headers.Authorization?.Scheme != "Bearer"
            || request.Headers.Authorization.Parameter == "speech-key" || request.Headers.UserAgent.ToString() != "EveConsole") throw new InvalidOperationException("Incorrect subscription identity.");
        if (Refusals-- > 0) return new(HttpStatusCode.Unauthorized) { Content = new StringContent("access-secret") };
        if (request.Method == HttpMethod.Get)
        {
            var query = request.RequestUri.Query;
            if (!query.StartsWith("?client_version=") || !Version.TryParse(query["?client_version=".Length..], out var version))
                throw new InvalidOperationException("The catalog request is missing a client version.");
            var catalog = version < MinimumCatalogVersion
                ? "{\"models\":[{\"slug\":\"hidden\",\"visibility\":\"hide\"}]}"
                : "{\"models\":[{\"slug\":\"test-codex\",\"display_name\":\"Test Codex\",\"visibility\":\"list\"},{\"slug\":\"hidden\",\"visibility\":\"hide\"}]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(catalog) };
        }
        var body = await request.Content!.ReadAsStringAsync(ct);
        Bodies.Add(body);
        Sessions.Add(request.Headers.GetValues("session_id").Single());
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.GetProperty("store").GetBoolean() || !json.RootElement.GetProperty("stream").GetBoolean()
            || json.RootElement.GetProperty("instructions").GetString() != "stable instructions") throw new InvalidOperationException("Missing Responses setup.");
        var call = new { type = "function_call", id = "fc-1", call_id = "call-1", name = "picture", arguments = "{}", status = "completed" };
        var isToolRound = Mode is "budget" or "incomplete" || (Mode == "tools" && Bodies.Count == 1);
        object[] output = isToolRound ? [new { type = "reasoning", id = "rs-1", encrypted_content = "opaque", summary = Array.Empty<object>() }, call] : [];
        var events = Mode == "failed" ? Event(new { type = "response.failed" })
            : isToolRound ? Event(new { type = "response.output_item.done", item = call })
            : Event(new { type = "response.output_text.delta", delta = "Hello." });
        if (Mode is not ("failed" or "truncated")) events += Event(new
        {
            type = "response.completed", response = new
            {
                status = Mode == "incomplete" ? "incomplete" : "completed", output,
                usage = new { input_tokens = 100, output_tokens = 4, input_tokens_details = new { cached_tokens = 25 } },
            },
        });
        return new(HttpStatusCode.OK) { Content = new StringContent(events) };
    }
    private static string Event(object data) => ": heartbeat\r\nevent: response\r\ndata:" + JsonSerializer.Serialize(data) + "\r\n\r\n";
}
sealed class PictureTool : IAgentTool
{
    public int Calls;
    public string Name => "picture";
    public string Description => "test picture";
    public object InputSchema => new { type = "object", properties = new { } };
    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default) => Task.FromResult("tool result");
    public Task<AgentToolResult> ExecuteWithResultAsync(JsonElement input, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new AgentToolResult { Text = "tool result", ImageBase64 = "aW1hZ2U=" });
    }
}
