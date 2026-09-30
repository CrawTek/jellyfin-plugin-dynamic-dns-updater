using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.DynDns;
public static class FreeDnsChecks
{
    public static async Task Run(Action<bool,string> assert)
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new FakeHandler { ExpectedHost = "freedns.afraid.org", Response = "Updated media.example.net to 8.8.8.8 in 0.001 seconds" };
        var engine = new UpdateEngine(new HttpClient(handler), () => now);
        var settings = new Settings { Provider = "FreeDNS", Enabled = true, Hostname = "media.example.net", Revision = "free" };
        const string key = "fakeKey+/==";
        var state = new UpdateState();
        await engine.Run(settings, key, state, () => {}, default);
        assert(state.Outcome == "success" && state.LastSuccess == now, "FreeDNS confirms matching host and IPv4 without username");
        assert(handler.LastUri!.AbsoluteUri == "https://freedns.afraid.org/dynamic/update.php?fakeKey%2B%2F%3D%3D&address=8.8.8.8" && handler.LastAuth == null, "FreeDNS key encoded at fixed HTTPS endpoint without Basic auth");
        int calls = handler.Requests;
        await engine.Run(settings, key, state, () => {}, default);
        assert(calls == handler.Requests, "FreeDNS manual checks respect cooldown");
        now = now.AddMinutes(11); int updates = handler.Updates;
        state = JsonSerializer.Deserialize<UpdateState>(JsonSerializer.Serialize(state))!;
        await engine.Run(settings, key, state, () => {}, default);
        assert(handler.Updates == updates, "FreeDNS unchanged IP skipped after restart");
        foreach (var body in new[] { "ERROR: Address 8.8.8.8 has not changed.", "Updated media.example.net to 8.8.8.8" })
        {
            handler.Response = body; state = new();
            await engine.Run(settings, key, state, () => {}, default);
            assert(state.Outcome == "success", "FreeDNS accepts confirmed unchanged or updated address");
        }
        foreach (var body in new[] { "ERROR: Address 1.1.1.1 has not changed.", "Updated wrong.example.net to 8.8.8.8", "Updated media.example.net to 1.1.1.1", "ERROR: Invalid key " + key, "good 8.8.8.8", "<html>login</html>" })
        {
            handler.Response = body; state = new();
            await engine.Run(settings, key, state, () => {}, default);
            assert(state.Suspended && state.LastSuccess == null && !JsonSerializer.Serialize(state).Contains(key), "FreeDNS rejects unconfirmed response without exposing secret");
            calls = handler.Requests; now = now.AddHours(1);
            await engine.Run(settings, key, state, () => {}, default);
            assert(calls == handler.Requests, "FreeDNS fatal error stops automatic retries");
        }
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            handler.HttpCode = status; handler.RetryDelay = TimeSpan.FromHours(1); state = new();
            await engine.Run(settings, key, state, () => {}, default);
            assert(!state.Suspended && state.NextAttempt == now.AddHours(1), "FreeDNS temporary failure respects provider backoff");
        }
        foreach (var invalid in new[] { "https://evil.example/?key", "key&address=1.1.1.1", "", "key with spaces" })
        {
            calls = handler.Requests;
            await engine.Run(settings, invalid, new(), () => {}, default);
            assert(handler.Requests == calls, "FreeDNS invalid key rejected before network access");
        }
        assert(UpdateEngine.KeyFile("FreeDNS") == "freedns-updater.key", "FreeDNS uses isolated credential file");
    }
}
