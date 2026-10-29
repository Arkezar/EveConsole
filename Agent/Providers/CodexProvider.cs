using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EveConsole.Agent.Tools;
using EveConsole.Localization;

namespace EveConsole.Agent.Providers;

/// <summary>ChatGPT subscription inference uses Responses, not OpenAI Chat Completions.</summary>
public sealed class CodexProvider(string accountId, string model) : IAgentProvider
{
    public const string ServiceName = "OpenAI Codex";
    public const string BaseUrl = "https://chatgpt.com/backend-api/codex/";
    private static HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private const int MaxToolRounds = 20;
    private const string CatalogClientVersion = "0.165.0";
    private const string BudgetExhausted = "NOT RUN: the tool budget for this turn is used up. Answer now from what you already found, and say what you did not get to.";

    public string ProviderName => ServiceName;
    public bool IsConfigured => model.Length > 0 && CodexAccount.IsSignedIn(accountId);

    public static async Task<IReadOnlyList<ModelListing>> ListModelsAsync(string accountId, CancellationToken ct = default)
    {
        // OpenAI hides newer models from old clients; track the supported upstream Codex release.
        using var response = await SendAsync(accountId, HttpMethod.Get, $"models?client_version={CatalogClientVersion}", null, null, ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return json.RootElement.GetProperty("models").EnumerateArray()
            .Where(m => Text(m, "visibility") != "hide")
            .Select(m => new ModelListing(Text(m, "slug"), Text(m, "display_name") is { Length: > 0 } name ? name : Text(m, "slug")))
            .Where(m => m.Id.Length > 0).ToArray();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { return (await ListModelsAsync(accountId, timeout.Token).ConfigureAwait(false)).Any(m => m.Id == model); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
    }

    public async IAsyncEnumerable<string> StreamAsync(string systemPrompt, IReadOnlyList<AgentMessage> history,
        IReadOnlyList<IAgentTool>? tools = null, Action<UsageReport>? onUsage = null,
        string? volatileContext = null, string? sessionId = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var input = new List<object>();
        if (!string.IsNullOrWhiteSpace(volatileContext)) input.Add(new { role = "developer", content = volatileContext });
        input.AddRange(history.Select(m => (object)new
        {
            role = m.Role == MessageRole.User ? "user" : "assistant", content = m.ContentForModel,
        }));
        var toolMap = (tools ?? []).ToDictionary(t => t.Name);
        sessionId ??= Guid.NewGuid().ToString("N");
        for (var round = 0; round <= MaxToolRounds + 1; round++)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var body = new Dictionary<string, object?>
            {
                ["model"] = model, ["instructions"] = systemPrompt, ["input"] = input,
                ["stream"] = true, ["store"] = false, ["include"] = new[] { "reasoning.encrypted_content" },
                ["prompt_cache_key"] = sessionId,
            };
            if (toolMap.Count > 0 && round <= MaxToolRounds)
                body["tools"] = toolMap.Values.Select(t => new
                {
                    type = "function", name = t.Name, description = t.Description, parameters = t.InputSchema, strict = false,
                }).ToArray();
            var serialized = JsonSerializer.Serialize(body);
            using var response = await SendAsync(accountId, HttpMethod.Post, "responses", serialized, sessionId, ct).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var output = new List<JsonElement>();
            JsonElement completed = default;
            var answer = new StringBuilder();
            await foreach (var item in ReadEventsAsync(reader, ct).ConfigureAwait(false))
            {
                var type = Text(item, "type");
                if (type is "response.output_text.delta" or "response.refusal.delta")
                {
                    var delta = Text(item, "delta");
                    answer.Append(delta);
                    yield return delta;
                }
                else if (type == "response.output_item.done") output.Add(item.GetProperty("item").Clone());
                else if (type is "response.completed" or "response.done" or "response.incomplete")
                {
                    completed = item.GetProperty("response").Clone();
                    if (completed.TryGetProperty("output", out var items)) output = items.EnumerateArray().Select(i => i.Clone()).ToList();
                    break;
                }
                else if (type is "error" or "response.failed")
                    throw new InvalidOperationException(SettingsText.CodexResponseFailed);
            }
            if (completed.ValueKind != JsonValueKind.Object) throw new IOException(SettingsText.CodexStreamInterrupted);
            if (Text(completed, "status") is "failed" or "cancelled") throw new InvalidOperationException(SettingsText.CodexResponseFailed);
            var calls = output.Where(i => Text(i, "type") == "function_call").ToArray();
            var incomplete = Text(completed, "status") == "incomplete";
            var stop = incomplete ? "max_tokens" : calls.Length > 0 ? "tool_use" : "end_turn";
            var measured = completed.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object;
            var cached = measured && usage.TryGetProperty("input_tokens_details", out var details) ? Number(details, "cached_tokens") : 0;
            onUsage?.Invoke(new UsageReport
            {
                Provider = ServiceName, Model = model, IsLocal = false, IsEstimated = !measured,
                InputTokens = measured ? Math.Max(0, Number(usage, "input_tokens") - cached) : serialized.Length / 4,
                OutputTokens = measured ? Number(usage, "output_tokens") : Math.Max(answer.Length, JsonSerializer.Serialize(output).Length) / 4,
                CacheReadTokens = cached, StopReason = stop, DurationMs = (int)started.ElapsedMilliseconds,
            });
            if (incomplete)
            {
                yield return "\n\n" + SettingsText.CodexAnswerTooLong;
                yield break;
            }
            if (calls.Length == 0) yield break;
            if (round > MaxToolRounds)
            {
                yield return "\n\n" + SettingsText.CodexBudgetExhausted;
                yield break;
            }
            // Keep complete output items, including encrypted reasoning, for stateless tool continuations.
            input.AddRange(output.Cast<object>());
            var images = new List<object>();
            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                AgentToolResult result;
                if (round == MaxToolRounds) result = BudgetExhausted;
                else
                {
                    var name = Text(call, "name");
                    try
                    {
                        using var args = JsonDocument.Parse(Text(call, "arguments") is { Length: > 0 } a ? a : "{}");
                        result = toolMap.TryGetValue(name, out var tool)
                            ? await tool.ExecuteWithResultAsync(args.RootElement, ct).ConfigureAwait(false)
                            : (AgentToolResult)$"Tool '{name}' is not available.";
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { result = $"Tool error: {ex.Message}"; }
                }
                input.Add(new { type = "function_call_output", call_id = Text(call, "call_id"), output = result.Text });
                if (result.ImageBase64 is not null)
                    images.Add(new { type = "input_image", image_url = $"data:{result.ImageMediaType};base64,{result.ImageBase64}" });
            }
            if (images.Count > 0) input.Add(new { role = "user", content = images });
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(string accountId, HttpMethod method, string path,
        string? body, string? sessionId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var tokens = await CodexAccount.GetAsync(accountId, ct, refresh: attempt > 0).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            request.Headers.Add("chatgpt-account-id", tokens.AccountId);
            request.Headers.Add("OpenAI-Beta", "responses=experimental");
            request.Headers.Add("originator", "EveConsole");
            request.Headers.UserAgent.ParseAdd("EveConsole");
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }
            if (sessionId is not null) request.Headers.Add("session_id", sessionId);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            response.Dispose();
            if (status == HttpStatusCode.Unauthorized && attempt == 0) continue;
            throw new HttpRequestException(string.Format(SettingsText.CodexRequestFailed, (int)status), null, status);
        }
    }

    private static async IAsyncEnumerable<JsonElement> ReadEventsAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    if (payload.Trim() == "[DONE]") yield break;
                    using var json = JsonDocument.Parse(payload);
                    yield return json.RootElement.Clone();
                }
                if (line is null) yield break;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(line[5..].TrimStart(' '));
        }
    }

    private static string Text(JsonElement value, string property) => value.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static long Number(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n : 0;
}
