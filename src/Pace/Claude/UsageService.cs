using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Pace.Claude;

/// Claude Code's OAuth login. Never logged or persisted by Pace.
public sealed record Credentials(string Token, string? Plan, DateTimeOffset? ExpiresAt);

/// Failure with a message fit to show the user. Previous data stays on screen.
public sealed class UsageException(string message) : Exception(message);

public static class ClaudeCredentials
{
    /// Claude Code on Windows keeps its login as plain JSON in `<config dir>\.credentials.json`.
    /// Config dir is %CLAUDE_CONFIG_DIR% if set, else %USERPROFILE%\.claude.
    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string CredentialsPath => Path.Combine(ConfigDir, ".credentials.json");

    /// Read-only; returns null when Claude Code isn't installed or isn't logged in with a subscription.
    /// FileShare.ReadWrite so we never block Claude Code while it rewrites the file.
    public static Credentials? Read(string? path = null)
    {
        try
        {
            using var fs = new FileStream(path ?? CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o) || o.ValueKind != JsonValueKind.Object) return null;
            if (!o.TryGetProperty("accessToken", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() is not { Length: > 0 } token) return null;
            string? plan = o.TryGetProperty("subscriptionType", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            DateTimeOffset? exp = o.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            return new Credentials(token, plan, exp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

/// Wraps the undocumented endpoint Claude Code's `/usage` uses. Everything endpoint-specific lives here
/// and in UsageParser, so an API change doesn't touch the UI.
public sealed class UsageService(HttpMessageHandler? handler = null, Func<Credentials?>? readCredentials = null, TimeProvider? clock = null)
{
    static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");

    // No redirects (token can't be forwarded to another host), no cookies/cache.
    readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    readonly Func<Credentials?> _read = readCredentials ?? (() => ClaudeCredentials.Read());
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// Last plan seen in the credentials ("max", "pro", ...), shown as the header badge.
    public string? Plan { get; private set; }

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct = default)
    {
        // Re-read every time: Claude Code refreshes the token itself and rewrites the file.
        var creds = await Task.Run(_read, ct) ?? throw new UsageException("No Claude Code login found. Run `claude` and log in.");
        Plan = creds.Plan;

        // Pace never refreshes the token: OAuth refresh rotates the refresh token, which would sign
        // Claude Code out. Skip the doomed request and wait for Claude Code to refresh it.
        if (creds.ExpiresAt is { } exp && exp <= _clock.GetUtcNow())
            throw new UsageException("Token expired. Open Claude Code once to refresh it.");

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.Token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("pace/1.0");

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new UsageException("Request timed out."); }
        catch (HttpRequestException) { throw new UsageException("Can't reach api.anthropic.com. Check your connection."); }

        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK: break;
                case HttpStatusCode.Unauthorized: throw new UsageException("Token expired. Open Claude Code once to refresh it.");
                case HttpStatusCode.TooManyRequests: throw new UsageException("Rate limited. Will retry on the next refresh.");
                default: throw new UsageException($"HTTP {(int)resp.StatusCode}");
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            try { return UsageParser.Parse(body); }
            catch (JsonException) { throw new UsageException("Unexpected response from the usage API."); }
        }
    }
}
