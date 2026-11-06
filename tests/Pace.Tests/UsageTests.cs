using System.Net;
using Pace.Claude;
using Xunit;

namespace Pace.Tests;

public class ParserTests
{
    [Fact]
    public void ParsesRealResponseShape()
    {
        var s = UsageParser.Parse(File.ReadAllText("Fixtures/usage.json"));
        Assert.Equal(["session", "weekly_all", "weekly_scoped"], s.Limits.Select(l => l.Kind));
        Assert.Equal("Current session (5h)", s.Limits[0].Title);
        Assert.Equal(37, s.Limits[0].Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T14:20:00.25+00:00"), s.Limits[0].ResetsAt);
        Assert.Equal("Weekly · all models", s.Limits[1].Title);
        Assert.Equal("Weekly · Fable", s.Limits[2].Title);
        Assert.Equal(12, s.Limits[2].Percent);
        // 0% surfaces (Cowork) hidden
        Assert.Equal(["Claude Code", "Chats"], s.Breakdown.Select(b => b.DisplayName));
    }

    [Theory]
    [InlineData("weekly_opus", "Weekly · Opus")]
    [InlineData("weekly_sonnet", "Weekly · Sonnet")]
    [InlineData("weekly_scoped", "Weekly Scoped")]
    [InlineData("some_new_kind", "Some New Kind")]
    public void Titles(string kind, string title) => Assert.Equal(title, new UsageLimit(kind, 1, null, null).Title);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"limits": null, "seven_day_breakdown": null}""")]
    [InlineData("""{"limits": [], "seven_day_breakdown": {"rows": null}}""")]
    public void MissingOrNullSectionsGiveEmpty(string json)
    {
        var s = UsageParser.Parse(json);
        Assert.Empty(s.Limits);
        Assert.Empty(s.Breakdown);
    }

    [Fact]
    public void SkipsEntriesWithoutKindOrPercent_NoInventedZeros()
    {
        var s = UsageParser.Parse("""
            {"limits": [
              {"kind": "session", "percent": null},
              {"percent": 40},
              {"kind": "weekly_all", "percent": 55.5, "resets_at": null},
              {"kind": "weekly_opus", "percent": 12, "resets_at": "not a date"}
            ]}
            """);
        Assert.Equal(2, s.Limits.Count);
        Assert.Equal(55.5, s.Limits[0].Percent);
        Assert.Null(s.Limits[0].ResetsAt);
        Assert.Null(s.Limits[1].ResetsAt);
    }
}

public class FormattingTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact] public void NoDate() => Assert.Equal("", UsageViewModel.ResetText(null, Now));
    [Fact] public void Past() => Assert.Equal("Resetting…", UsageViewModel.ResetText(Now.AddSeconds(-5), Now));
    [Fact] public void Exactly() => Assert.Equal("Resetting…", UsageViewModel.ResetText(Now, Now));
    [Fact] public void Minutes() => Assert.Equal("Resets in 42m", UsageViewModel.ResetText(Now.AddMinutes(42).AddSeconds(30), Now));
    [Fact] public void Hours() => Assert.Equal("Resets in 3h 5m", UsageViewModel.ResetText(Now.AddHours(3).AddMinutes(5), Now));
    [Fact] public void JustUnderADay() => Assert.Equal("Resets in 23h 59m", UsageViewModel.ResetText(Now.AddSeconds(86399), Now));

    [Fact]
    public void ADayOrMoreShowsWeekdayAndTime()
    {
        var d = Now.AddDays(3);
        Assert.Equal("Resets " + d.ToLocalTime().ToString("ddd h:mm tt"), UsageViewModel.ResetText(d, Now));
    }

    [Theory]
    [InlineData(0, "Normal")] [InlineData(69.9, "Normal")] [InlineData(70, "Warning")]
    [InlineData(89.9, "Warning")] [InlineData(90, "Critical")] [InlineData(120, "Critical")]
    public void Severity(double pct, string s) => Assert.Equal(s, UsageViewModel.Severity(pct));

}

public class ServiceTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++; Last = r;
            return Task.FromResult(f(r));
        }
    }

    static Credentials Valid => new("tok-123", "max", DateTimeOffset.UtcNow.AddHours(1));
    static HttpResponseMessage Ok(string body = "{\"limits\":[]}") => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task SendsExpectedRequest()
    {
        var h = new Handler(_ => Ok(File.ReadAllText("Fixtures/usage.json")));
        var svc = new UsageService(h, () => Valid);
        var s = await svc.FetchAsync();
        Assert.Equal(3, s.Limits.Count);
        Assert.Equal("max", svc.Plan);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", h.Last!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, h.Last.Method);
        Assert.Equal("Bearer", h.Last.Headers.Authorization!.Scheme);
        Assert.Equal("tok-123", h.Last.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", Assert.Single(h.Last.Headers.GetValues("anthropic-beta")));
    }

    [Fact]
    public async Task MissingCredentials()
    {
        var h = new Handler(_ => Ok());
        var e = await Assert.ThrowsAsync<UsageException>(() => new UsageService(h, () => null).FetchAsync());
        Assert.Contains("No Claude Code login", e.Message);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task ExpiredCredentialsSkipTheRequest()
    {
        var h = new Handler(_ => Ok());
        var creds = Valid with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var e = await Assert.ThrowsAsync<UsageException>(() => new UsageService(h, () => creds).FetchAsync());
        Assert.Contains("expired", e.Message);
        Assert.Equal(0, h.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "expired")]
    [InlineData(HttpStatusCode.TooManyRequests, "Rate limited")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData(HttpStatusCode.Found, "HTTP 302")] // redirects are never followed
    public async Task HttpErrors(HttpStatusCode code, string msg)
    {
        var svc = new UsageService(new Handler(_ => new HttpResponseMessage(code)), () => Valid);
        var e = await Assert.ThrowsAsync<UsageException>(() => svc.FetchAsync());
        Assert.Contains(msg, e.Message);
    }

    [Fact]
    public async Task NetworkFailure()
    {
        var svc = new UsageService(new Handler(_ => throw new HttpRequestException("dns")), () => Valid);
        var e = await Assert.ThrowsAsync<UsageException>(() => svc.FetchAsync());
        Assert.Contains("Can't reach", e.Message);
    }

    [Fact]
    public async Task MalformedBody()
    {
        var svc = new UsageService(new Handler(_ => Ok("<html>")), () => Valid);
        var e = await Assert.ThrowsAsync<UsageException>(() => svc.FetchAsync());
        Assert.Contains("Unexpected response", e.Message);
    }

    [Fact]
    public async Task ErrorMessagesNeverContainToken()
    {
        // Messages are fixed strings; this guards against someone interpolating the request later.
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var e = await Assert.ThrowsAsync<UsageException>(() => new UsageService(h, () => Valid).FetchAsync());
        Assert.DoesNotContain("tok-123", e.Message);
    }
}

public class CredentialsTests
{
    static string Temp(string content)
    {
        var p = Path.GetTempFileName();
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void ReadsClaudeAiOauth()
    {
        var p = Temp("""{"mcpOAuth": {}, "claudeAiOauth": {"accessToken": "a", "refreshToken": "r", "expiresAt": 1793969120303, "subscriptionType": "max"}}""");
        var c = ClaudeCredentials.Read(p)!;
        Assert.Equal("a", c.Token);
        Assert.Equal("max", c.Plan);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793969120303), c.ExpiresAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"claudeAiOauth": null}""")]
    [InlineData("""{"claudeAiOauth": {"accessToken": ""}}""")]
    [InlineData("not json")]
    public void InvalidGivesNull(string json) => Assert.Null(ClaudeCredentials.Read(Temp(json)));

    [Fact]
    public void MissingFileGivesNull() => Assert.Null(ClaudeCredentials.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
}
