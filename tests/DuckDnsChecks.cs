using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.DynDns;

public static class DuckDnsChecks
{
    public static async Task Run(Action<bool, string> assert)
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new FakeHandler { ExpectedHost = "www.duckdns.org", Response = "OK\n8.8.8.8\n\nUPDATED" };
        var engine = new UpdateEngine(new HttpClient(handler), () => now);
        var settings = new Settings { Provider = "DuckDNS", Enabled = true, Hostname = "sample.duckdns.org", Revision = "duck" };
        const string token = "test-token&secret=value";
        var state = new UpdateState();
        await engine.Run(settings, token, state, () => {}, default);
        assert(state.LastSuccess == now && state.Outcome == "success" && !state.Suspended, "DuckDNS confirms supplied IPv4 without username");
        assert(handler.LastUri!.AbsoluteUri == "https://www.duckdns.org/update?domains=sample&token=test-token%26secret%3Dvalue&ip=8.8.8.8&verbose=true" && handler.LastAuth == null,
            "DuckDNS encodes token and sends only domain, token, IPv4 and verbose parameters");
        assert(!JsonSerializer.Serialize(state).Contains(token), "DuckDNS status excludes token");
        int updates = handler.Updates; now = now.AddMinutes(11);
        state = JsonSerializer.Deserialize<UpdateState>(JsonSerializer.Serialize(state))!;
        await engine.Run(settings, token, state, () => {}, default);
        assert(handler.Updates == updates, "DuckDNS unchanged IPv4 skipped after restart");
        settings.Revision = "new-token"; now = now.AddMinutes(11);
        await engine.Run(settings, token, state, () => {}, default);
        assert(handler.Updates == updates + 1, "DuckDNS changed credential revalidates");
        foreach (var response in new[] { "OK\r\n8.8.8.8\r\n\r\nNOCHANGE\r\n", "OK\n8.8.8.8\n2001:db8::1\nUPDATED" })
        {
            handler.Response = response; state = new();
            await engine.Run(settings, token, state, () => {}, default);
            assert(state.LastSuccess == now, "DuckDNS verbose confirmation accepted with blank or existing IPv6");
        }
        foreach (var response in new[] { "KO", "OK", "OK\n1.1.1.1\n\nUPDATED", "OK\n\n\nUPDATED", "OK\n8.8.8.8\n\nUNKNOWN", "good 8.8.8.8", "unexpected " + token })
        {
            handler.Response = response; state = new();
            await engine.Run(settings, token, state, () => {}, default);
            assert(state.Suspended && state.Outcome == "error" && state.LastSuccess == null && !state.Message.Contains(token), "DuckDNS rejects failed or unconfirmed response safely");
            int calls = handler.Requests; now = now.AddHours(1);
            await engine.Run(settings, token, state, () => {}, default);
            assert(handler.Requests == calls, "DuckDNS paused state stops all requests");
        }
        foreach (var code in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests })
        {
            handler.HttpCode = code; handler.RetryDelay = TimeSpan.FromHours(1); state = new();
            await engine.Run(settings, token, state, () => {}, default);
            assert(state.Outcome == "error" && !state.Suspended && state.NextAttempt == now.AddHours(1), "DuckDNS temporary failure honors Retry-After");
            int calls = handler.Requests;
            await engine.Run(settings, token, state, () => {}, default);
            assert(handler.Requests == calls, "DuckDNS cooldown cannot be bypassed");
        }
        foreach (var host in new[] { "example.com", "a.b.duckdns.org", "duckdns.org", "x.duckdns.org.evil.com", "one,two", "https://x.duckdns.org" })
        {
            settings.Hostname = host; int calls = handler.Requests;
            await engine.Run(settings, token, new(), () => {}, default);
            assert(handler.Requests == calls, "DuckDNS rejects invalid target: " + host);
        }
        assert(UpdateEngine.NormalizeHostname("DuckDNS", " Sample ") == "sample.duckdns.org" &&
            UpdateEngine.NormalizeHostname("DuckDNS", "Sample.DuckDNS.org") == "sample.duckdns.org", "DuckDNS accepts short or full hostname");
    }
}
