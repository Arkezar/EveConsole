using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveConsole.Localization;
using EveConsole.Services;

namespace EveConsole.Agent.Providers;

/// <summary>Profile-local ChatGPT login. Tokens never enter agent settings or the shared database.</summary>
public static class CodexAccount
{
    private const string AuthUrl = "https://auth.openai.com/";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string VerificationUrl = AuthUrl + "codex/device";
    private static HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object ReadGate = new();
    private static string _cachedPath = "";
    private static DateTime _cachedWrite;
    private static Tokens? _cachedTokens;
    private static string TokenPath => Path.Combine(AppConfig.AppDataDir, "codex-account.json");
    private static string SecretReference => "codex-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TokenPath)));

    public sealed record DeviceCode(string Id, string Code, int Interval);
    public sealed class Tokens
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string AccountId { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
    }

    public static async Task<DeviceCode> BeginAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(AuthUrl + "api/accounts/deviceauth/usercode", new { client_id = ClientId }, ct).ConfigureAwait(false);
        CheckResponse(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = json.RootElement;
        var interval = root.TryGetProperty("interval", out var i) && int.TryParse(i.ToString(), out var seconds) ? Math.Clamp(seconds, 1, 60) : 5;
        var code = root.TryGetProperty("user_code", out var c) ? c.GetString() : root.GetProperty("usercode").GetString();
        return new(root.GetProperty("device_auth_id").GetString()!, code!, interval);
    }

    public static async Task<string> CompleteAsync(DeviceCode device, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        while (true)
        {
            using var response = await _http.PostAsJsonAsync(AuthUrl + "api/accounts/deviceauth/token",
                new { device_auth_id = device.Id, user_code = device.Code }, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                await Task.Delay(TimeSpan.FromSeconds(device.Interval), timeout.Token).ConfigureAwait(false);
                continue;
            }
            CheckResponse(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var tokens = await ExchangeAsync(new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = ClientId,
                ["code"] = json.RootElement.GetProperty("authorization_code").GetString()!,
                ["code_verifier"] = json.RootElement.GetProperty("code_verifier").GetString()!,
                ["redirect_uri"] = AuthUrl + "deviceauth/callback",
            }, null, timeout.Token).ConfigureAwait(false);
            await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                Save(tokens);
                return tokens.AccountId;
            }
            finally { Gate.Release(); }
        }
    }

    public static bool IsSignedIn(string accountId) => accountId.Length > 0 && Read()?.AccountId == accountId;

    public static async Task<Tokens> GetAsync(string accountId, CancellationToken ct, bool refresh = false)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tokens = Read();
            if (tokens is null || tokens.AccountId != accountId)
                throw new HttpRequestException(SettingsText.CodexNeedSignIn, null, HttpStatusCode.Unauthorized);
            if (refresh || tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                tokens = await ExchangeAsync(new()
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = tokens.RefreshToken,
                }, tokens, ct).ConfigureAwait(false);
                Save(tokens);
            }
            return tokens;
        }
        finally { Gate.Release(); }
    }

    public static async Task SignOutAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Overwrite any keyring entry as well as removing its local reference.
            if (File.Exists(TokenPath)) SecretStore.Protect("{}", SecretReference);
            File.Delete(TokenPath);
            lock (ReadGate) { _cachedTokens = null; _cachedPath = ""; }
        }
        finally { Gate.Release(); }
    }

    private static Tokens? Read()
    {
        lock (ReadGate)
        {
            try
            {
                if (!File.Exists(TokenPath)) return null;
                var written = File.GetLastWriteTimeUtc(TokenPath);
                // Status bindings must not launch a keyring process on every property read.
                if (_cachedPath == TokenPath && _cachedWrite == written) return _cachedTokens;
                var json = SecretStore.Unprotect(File.ReadAllText(TokenPath));
                var tokens = json is null ? null : JsonSerializer.Deserialize<Tokens>(json);
                _cachedPath = TokenPath;
                _cachedWrite = written;
                return _cachedTokens = tokens is { AccessToken.Length: > 0, RefreshToken.Length: > 0, AccountId.Length: > 0 } ? tokens : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    private static void Save(Tokens tokens)
    {
        Directory.CreateDirectory(AppConfig.AppDataDir);
        var protectedTokens = SecretStore.Protect(JsonSerializer.Serialize(tokens), SecretReference);
        var temporary = TokenPath + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            using (var writer = new StreamWriter(file)) writer.Write(protectedTokens);
            File.Move(temporary, TokenPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<Tokens> ExchangeAsync(Dictionary<string, string> form, Tokens? previous, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(AuthUrl + "oauth/token", content, ct).ConfigureAwait(false);
        CheckResponse(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = json.RootElement;
        var access = root.GetProperty("access_token").GetString()!;
        // JWT claims select routing and expiry only; OpenAI validates the token, not this client.
        var payload = access.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
        var accountId = claims.RootElement.GetProperty("https://api.openai.com/auth").GetProperty("chatgpt_account_id").GetString()!;
        if (string.IsNullOrWhiteSpace(accountId) || (previous is not null && previous.AccountId != accountId))
            throw new InvalidOperationException(SettingsText.CodexNeedSignIn);
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : previous?.RefreshToken;
        if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException(SettingsText.CodexNeedSignIn);
        return new()
        {
            AccessToken = access, RefreshToken = refresh, AccountId = accountId,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(claims.RootElement.GetProperty("exp").GetInt64()),
        };
    }

    private static void CheckResponse(HttpResponseMessage response)
    {
        // Auth error bodies can contain credentials. Never surface or log them.
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(string.Format(SettingsText.CodexAuthFailed, (int)response.StatusCode), null, response.StatusCode);
    }
}
