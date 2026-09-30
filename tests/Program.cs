using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.DynDns;

int passed = 0;
void Assert(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
var now = DateTimeOffset.Parse("2026-09-30T00:00:00Z");
var settings = new Settings { Enabled = true, Hostname = "media.example.net", Username = "test-user", Revision = "first" };
foreach (var ip in new[] { "127.0.0.1", "192.168.1.10", "100.64.0.1", "169.254.1.1", "10.1.1.1", "172.16.1.1", "203.0.113.1", "224.0.0.1", "::1", "1.2.3", "bad" })
    Assert(!UpdateEngine.PublicV4(ip), "Reject non-public or invalid IP " + ip);
Assert(UpdateEngine.PublicV4("8.8.8.8"), "Accept public IPv4");
Assert(!UpdateEngine.ValidHostname("one.example,two.example") && !UpdateEngine.ValidHostname("https://example.org"), "Reject multiple hosts and URLs");
var handler = new FakeHandler();
var engine = new UpdateEngine(new HttpClient(handler), () => now);
var state = new UpdateState();
int writes = 0;
Action persist = () => writes++;
await engine.Run(settings, "secret-test-key", state, persist, default);
Assert(handler.Updates == 1 && state.LastSuccess == now && state.Outcome == "success", "First update confirms credentials");
Assert(handler.AuthSeen && handler.UserAgentSeen, "Authentication and identifying User-Agent supplied");
await engine.Run(settings, "secret-test-key", state, persist, default);
Assert(handler.Requests == 2, "Repeated click cannot bypass cooldown");
Assert(state.NextAttempt == now.AddMinutes(10) && new DnsTask().GetDefaultTriggers().Single().IntervalTicks == TimeSpan.FromMinutes(10).Ticks,
    "Default schedule and normal cooldown are both ten minutes");
now = now.AddSeconds(599);
await engine.Run(settings, "secret-test-key", state, persist, default);
Assert(handler.Requests == 2, "Normal check waits the full ten minutes");
now = now.AddSeconds(1);
await engine.Run(settings, "secret-test-key", state, persist, default);
Assert(handler.Requests == 3 && handler.Updates == 1, "Check allowed at ten minutes; unchanged IP still not sent");
now = now.AddMinutes(11);
state = JsonSerializer.Deserialize<UpdateState>(JsonSerializer.Serialize(state))!;
await engine.Run(settings, "secret-test-key", state, persist, default);
Assert(handler.Updates == 1, "Unchanged IP skipped after state reload");
now = now.AddMinutes(11); handler.Ip = "8.8.4.4";
await engine.Run(settings, "secret-test-key", state, persist, default);
Assert(handler.Updates == 2 && state.ConfirmedIp == "8.8.4.4", "Changed IP updates");
now = now.AddMinutes(11); settings.Revision = "second";
await engine.Run(settings, "secret-test-key", state, persist, default);
Assert(handler.Updates == 3, "Changed configuration revalidates unchanged IP once");
foreach (var code in new[] { "badauth", "nohost", "abuse", "badagent", "notfqdn", "garbage", "good 127.0.0.1" })
{
    var bad = new UpdateState(); handler.Response = code;
    await engine.Run(settings, "secret-test-key", bad, persist, default);
    Assert(bad.Suspended && bad.LastSuccess == null && bad.Outcome == "error", "Fatal response pauses: " + code);
    int count = handler.Requests; now = now.AddHours(1);
    await engine.Run(settings, "secret-test-key", bad, persist, default);
    Assert(handler.Requests == count, "No repeated request after " + code);
}
foreach (var code in new[] { "911", "dnserr" })
{
    var temporary = new UpdateState(); handler.Response = code;
    await engine.Run(settings, "secret-test-key", temporary, persist, default);
    Assert(temporary.Outcome == "error" && !temporary.Suspended && temporary.NextAttempt >= now.AddMinutes(30), "Provider backoff: " + code);
}
handler.Response = null; handler.HttpCode = HttpStatusCode.TooManyRequests;
var rateLimited = new UpdateState();
await engine.Run(settings, "secret-test-key", rateLimited, persist, default);
Assert(rateLimited.NextAttempt >= now.AddMinutes(30), "HTTP rate-limit backoff");
handler.HttpCode = HttpStatusCode.OK; handler.Response = "nochg 8.8.4.4";
var noChange = new UpdateState();
await engine.Run(settings, "secret-test-key", noChange, persist, default);
Assert(noChange.LastSuccess == now && !noChange.Suspended, "Initial nochg accepted");
var disabled = new Settings(); int before = handler.Requests;
await engine.Run(disabled, "", new UpdateState(), persist, default);
Assert(handler.Requests == before, "Disabled plugin makes no network requests");
handler.Ip = "192.168.1.10"; int updates = handler.Updates;
await engine.Run(settings, "secret-test-key", new UpdateState(), persist, default);
Assert(handler.Updates == updates, "Private detected IP never sent to Dyn");
Assert(writes > 0 && !JsonSerializer.Serialize(state).Contains("secret-test-key"), "Persisted status excludes credentials");

Assert(JsonSerializer.Deserialize<Settings>("{}")!.Provider == "Dyn", "Legacy settings default to Dyn");
var noip = new Settings { Provider = "NoIP", Enabled = true, Hostname = "all.ddnskey.com", Username = "key-user", Revision = "noip-first" };
handler.ExpectedHost = "dynupdate.no-ip.com"; handler.Ip = "8.8.8.8"; handler.Response = null;
var ns = new UpdateState();
await engine.Run(noip, "noip-test-password", ns, persist, default);
Assert(ns.LastSuccess == now && ns.Outcome == "success" && ns.Message.StartsWith("No-IP"), "No-IP update confirms public IP");
Assert(handler.LastUri!.AbsoluteUri == "https://dynupdate.no-ip.com/nic/update?hostname=all.ddnskey.com&myip=8.8.8.8", "No-IP request has only hostname and IPv4 parameters");
Assert(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(handler.LastAuth!)) == "key-user:noip-test-password", "No-IP DDNS Key uses Basic auth");
Assert(handler.UserAgentSeen, "No-IP receives identifying User-Agent");
int sent = handler.Updates;
now = now.AddMinutes(11);
ns = JsonSerializer.Deserialize<UpdateState>(JsonSerializer.Serialize(ns))!;
await engine.Run(noip, "noip-test-password", ns, persist, default);
Assert(handler.Updates == sent, "No-IP unchanged IP skipped after restart");
noip.Revision = "noip-second"; now = now.AddMinutes(11);
await engine.Run(noip, "noip-test-password", ns, persist, default);
Assert(handler.Updates == sent + 1, "No-IP credential change revalidates");
foreach (var code in new[] { "badauth", "nohost", "badagent", "abuse", "!donator", "garbage", "good", "good 1.1.1.1", "good 8.8.8.8\nnohost" })
{
    handler.Response = code; var bad = new UpdateState();
    await engine.Run(noip, "noip-test-password", bad, persist, default);
    Assert(bad.Suspended && bad.LastSuccess == null && bad.Outcome == "error", "No-IP pauses on " + code);
    int count = handler.Requests; now = now.AddHours(1);
    await engine.Run(noip, "noip-test-password", bad, persist, default);
    Assert(handler.Requests == count, "No-IP suspended state stops all requests");
}
handler.Response = "nochg 8.8.8.8";
var unchanged = new UpdateState();
await engine.Run(noip, "noip-test-password", unchanged, persist, default);
Assert(unchanged.LastSuccess == now, "No-IP nochg confirmation accepted");
foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests })
{
    handler.HttpCode = status; handler.Response = "911";
    var retry = new UpdateState();
    await engine.Run(noip, "noip-test-password", retry, persist, default);
    Assert(!retry.Suspended && retry.NextAttempt == now.AddMinutes(30), "No-IP temporary failure backs off: " + status);
}
handler.RetryDelay = TimeSpan.FromHours(2);
var cooldown = new UpdateState();
await engine.Run(noip, "noip-test-password", cooldown, persist, default);
Assert(cooldown.NextAttempt == now.AddHours(2), "No-IP honors Retry-After duration");
handler.RetryDelay = null; handler.RetryDate = now.AddHours(3);
await engine.Run(noip, "noip-test-password", new UpdateState(), () => {}, default);
var dated = new UpdateState();
await engine.Run(noip, "noip-test-password", dated, persist, default);
Assert(dated.NextAttempt == now.AddHours(3), "No-IP honors Retry-After date");
int calls = handler.Requests;
await engine.Run(noip, "noip-test-password", cooldown, persist, default);
Assert(handler.Requests == calls, "No-IP repeated checks cannot bypass backoff");
noip.Provider = "Unknown";
await engine.Run(noip, "noip-test-password", new UpdateState(), persist, default);
Assert(handler.Requests == calls, "Unknown provider never receives credentials");

var dynuSettings = new Settings { Provider = "Dynu", Enabled = true, Hostname = "sample.dynu.com", Username = "dynu-user", Revision = "dynu" };
handler.ExpectedHost = "api.dynu.com"; handler.HttpCode = HttpStatusCode.OK; handler.Response = "good";
handler.RetryDate = null; handler.RetryDelay = null;
var dynuState = new UpdateState();
await engine.Run(dynuSettings, "dynu-test-password", dynuState, persist, default);
Assert(dynuState.Outcome == "success", "Dynu accepts documented bare success");
Assert(handler.LastUri!.AbsoluteUri == "https://api.dynu.com/nic/update?hostname=sample.dynu.com&myip=8.8.8.8&myipv6=no", "Dynu targets one hostname and explicitly preserves IPv6");
Assert(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(handler.LastAuth!)) == "dynu-user:dynu-test-password", "Dynu credential is in Basic auth, not URL");
foreach (var response in new[] { "nochg", "good 8.8.8.8", "nochg 8.8.8.8" }) {
 handler.Response = response; var result = new UpdateState();
 await engine.Run(dynuSettings, "dynu-test-password", result, persist, default);
 Assert(result.Outcome == "success", "Dynu success: " + response);
}
foreach (var response in new[] { "unknown", "badauth", "abuse", "nohost", "notfqdn", "numhost", "!donator", "good 1.1.1.1", "good extra data" }) {
 handler.Response = response; var result = new UpdateState();
 await engine.Run(dynuSettings, "dynu-test-password", result, persist, default);
 Assert(result.Suspended && result.Outcome == "error", "Dynu pauses on invalid response: " + response);
}
foreach (var response in new[] { "servererror", "dnserr", "911" }) {
 handler.Response = response; var result = new UpdateState();
 await engine.Run(dynuSettings, "dynu-test-password", result, persist, default);
 Assert(!result.Suspended && result.NextAttempt == now.AddMinutes(30), "Dynu temporary error backoff: " + response);
}
handler.Response = "good"; handler.RetryDelay = TimeSpan.FromHours(2); handler.HttpCode = HttpStatusCode.TooManyRequests;
var dynuLimited = new UpdateState();
await engine.Run(dynuSettings, "dynu-test-password", dynuLimited, persist, default);
Assert(dynuLimited.NextAttempt == now.AddHours(2), "Dynu respects longer Retry-After");
int dynuCalls = handler.Requests;
await engine.Run(dynuSettings, "dynu-test-password", dynuLimited, persist, default);
Assert(handler.Requests == dynuCalls, "Dynu cooldown blocks repeated checks");
handler.HttpCode = HttpStatusCode.OK; handler.RetryDelay = null; now = now.AddMinutes(11);
await engine.Run(dynuSettings, "dynu-test-password", dynuState, persist, default);
Assert(handler.Updates > 0 && dynuState.Message.StartsWith("IP address unchanged"), "Dynu unchanged address skipped");
await ControllerChecks.Run(Assert);
await DuckDnsChecks.Run(Assert);
await FreeDnsChecks.Run(Assert);

Console.WriteLine($"{passed} checks passed; no live DNS changes made.");

sealed class FakeHandler : HttpMessageHandler
{
    public string ExpectedHost = "members.dyndns.org";
    public Uri? LastUri;
    public string? LastAuth;
    public TimeSpan? RetryDelay;
    public DateTimeOffset? RetryDate;
    public int Requests, Updates;
    public string Ip = "8.8.8.8";
    public string? Response;
    public HttpStatusCode HttpCode = HttpStatusCode.OK;
    public bool AuthSeen, UserAgentSeen;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (request.RequestUri!.Host == "api.ipify.org")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ip) });
        if (request.RequestUri.Host != ExpectedHost || request.RequestUri.Scheme != "https") throw new Exception("Unexpected destination");
        LastUri = request.RequestUri; LastAuth = request.Headers.Authorization?.Parameter;
        Updates++; AuthSeen = request.Headers.Authorization?.Scheme == "Basic";
        UserAgentSeen = request.Headers.UserAgent.ToString().Contains("CrawTek Jellyfin-DynDNS/1.4.0.1");
        var response = new HttpResponseMessage(HttpCode) { Content = new StringContent(Response ?? "good " + Ip) };
        if (RetryDelay is { } delay) response.Headers.RetryAfter = new(delay);
        if (RetryDate is { } date) response.Headers.RetryAfter = new(date);
        return Task.FromResult(response);
    }
}


