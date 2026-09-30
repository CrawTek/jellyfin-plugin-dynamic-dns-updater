using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DynDns;

public sealed class Settings
{
    public string Provider { get; set; } = "Dyn";
    public bool Enabled { get; set; }
    public string Hostname { get; set; } = "";
    public string Username { get; set; } = "";
    public string Revision { get; set; } = "";
}

public sealed class UpdateState
{
    public string PublicIp { get; set; } = "";
    public string ConfirmedIp { get; set; } = "";
    public string ConfirmedRevision { get; set; } = "";
    public string Outcome { get; set; } = "pending";
    public string Message { get; set; } = "Not checked yet.";
    public DateTimeOffset? LastCheck { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? NextAttempt { get; set; }
    public bool Suspended { get; set; }
    public bool RequiresManualResume { get; set; } = true;
}

public sealed class UpdateEngine(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;
    public static bool ValidProvider(string provider) => provider is "Dyn" or "NoIP" or "DuckDNS" or "Dynu" or "FreeDNS";
    public static string KeyFile(string provider) => provider switch
    {
        "Dyn" => "updater.key", // Preserve existing installations without migrating their secret.
        "NoIP" => "noip-updater.key",
        "DuckDNS" => "duckdns-updater.key",
        "Dynu" => "dynu-updater.key",
        "FreeDNS" => "freedns-updater.key",
        _ => throw new ArgumentException("Unsupported DNS provider.")
    };
    public static bool ValidHostname(string host) => host.Length <= 253 && host.Contains('.') &&
        host.Split('.').All(label => Regex.IsMatch(label, @"^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$"));

    public static string NormalizeHostname(string provider, string host)
    {
        host = host.Trim().ToLowerInvariant();
        return provider == "DuckDNS" && host.Length > 0 && !host.Contains('.') ? host + ".duckdns.org" : host;
    }
    public static bool ValidProviderHostname(string provider, string host) => ValidHostname(host) &&
        (provider != "DuckDNS" || (host.EndsWith(".duckdns.org", StringComparison.Ordinal) && host.Split('.').Length == 3));

    public static bool ValidFreeDnsKey(string key) => Regex.IsMatch(key, @"^[A-Za-z0-9+/=_-]{8,1024}$");

    public static bool PublicV4(string input)
    {
        if (!Regex.IsMatch(input, @"^\d{1,3}(\.\d{1,3}){3}$") ||
            !IPAddress.TryParse(input, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2))) &&
            !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    public async Task Run(Settings settings, string secret, UpdateState state, Action persist, CancellationToken ct)
    {
        if (!settings.Enabled) { state.Outcome = "disabled"; state.Message = "Automatic updates are disabled."; persist(); return; }
        if (state.Suspended) return;
        if (state.NextAttempt > Now) return;
        state.Outcome = "pending";
        state.LastCheck = Now;
        // Persist the throttle before any network request; restarts and repeated button clicks cannot bypass it.
        state.NextAttempt = Now.Add(CheckInterval);
        persist();
        if (!ValidProvider(settings.Provider) || !ValidProviderHostname(settings.Provider, settings.Hostname) || (settings.Provider is not ("DuckDNS" or "FreeDNS") && string.IsNullOrWhiteSpace(settings.Username)) ||
            settings.Username.Contains(':') || string.IsNullOrWhiteSpace(secret) || (settings.Provider == "FreeDNS" && !ValidFreeDnsKey(secret)))
        {
            state.Message = "Select a supported provider and enter a valid hostname, username, and updater credential.";
            state.Suspended = true; state.RequiresManualResume = false; state.Outcome = "error"; persist(); return;
        }
        var provider = settings.Provider == "NoIP" ? "No-IP" : settings.Provider;
        try
        {
            using var detection = await http.GetAsync("https://api.ipify.org", ct).ConfigureAwait(false);
            if (!detection.IsSuccessStatusCode) throw new HttpRequestException();
            var ip = (await detection.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (!PublicV4(ip))
            {
                state.Outcome = "error"; state.Message = "Public IPv4 detection failed. No DNS update was sent."; persist(); return;
            }
            state.PublicIp = ip;
            if (state.ConfirmedIp == ip && state.ConfirmedRevision == settings.Revision)
            {
                state.Outcome = "success"; state.Message = "IP address unchanged. No update needed."; persist(); return;
            }
            var duck = settings.Provider == "DuckDNS";
            var free = settings.Provider == "FreeDNS";
            // DuckDNS requires the token in the HTTPS query. Never log request URLs or exceptions.
            var url = free
                ? "https://freedns.afraid.org/dynamic/update.php?" + Uri.EscapeDataString(secret) + "&address=" + Uri.EscapeDataString(ip)
                : duck
                ? "https://www.duckdns.org/update?domains=" + Uri.EscapeDataString(settings.Hostname[..^12]) +
                    "&token=" + Uri.EscapeDataString(secret) + "&ip=" + Uri.EscapeDataString(ip) + "&verbose=true"
                : (settings.Provider == "Dynu" ? "https://api.dynu.com/nic/update" : settings.Provider == "NoIP" ? "https://dynupdate.no-ip.com/nic/update" : "https://members.dyndns.org/nic/update") +
                    "?hostname=" + Uri.EscapeDataString(settings.Hostname) + "&myip=" + Uri.EscapeDataString(ip) +
                    (settings.Provider == "Dyn" ? "&wildcard=NOCHG&mx=NOCHG&backmx=NOCHG" : settings.Provider == "Dynu" ? "&myipv6=no" : "");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!duck && !free) request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username + ":" + secret)));
            request.Headers.UserAgent.ParseAdd("CrawTek Jellyfin-DynDNS/1.4.0.1 (+https://github.com/steve19ohio-byte/jellyfin-plugin-dyndns/issues)");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var code = parts.FirstOrDefault() ?? "";
            if (response.StatusCode == HttpStatusCode.Unauthorized || code == "badauth")
                Fatal(state, $"{provider} rejected the credentials. Correct and save the credentials or hostname to resume.", false);
            else if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests || code == "911" || (settings.Provider is "Dyn" or "Dynu" && code == "dnserr") || (settings.Provider == "Dynu" && code == "servererror"))
            {
                state.Outcome = "error";
                state.NextAttempt = Now.AddMinutes(30);
                state.Message = $"{provider} is temporarily unavailable. Updates paused for at least 30 minutes.";
                if (response.Headers.RetryAfter?.Delta is { } delay && Now.Add(delay) > state.NextAttempt)
                    state.NextAttempt = Now.Add(delay);
                if (response.Headers.RetryAfter?.Date is { } date && date > state.NextAttempt)
                    state.NextAttempt = date;
            }
            else if (free)
            {
                var updated = Regex.IsMatch(body, @"^Updated " + Regex.Escape(settings.Hostname) + @" to " + Regex.Escape(ip) + @"(?: in [0-9.]+ seconds)?\.?$", RegexOptions.IgnoreCase);
                var unchanged = body == $"ERROR: Address {ip} has not changed.";
                if (response.IsSuccessStatusCode && (updated || unchanged))
                {
                    state.ConfirmedIp = ip; state.ConfirmedRevision = settings.Revision; state.LastSuccess = Now; state.Outcome = "success";
                    state.Message = updated ? "FreeDNS confirmed the new IP address." : "FreeDNS confirmed the keyed record already has this IP address.";
                }
                else Fatal(state, "FreeDNS did not confirm the update. Check that the Direct URL key belongs to this hostname, resolve any provider issue, then save settings to retry.");
            }
            else if (duck)
            {
                var lines = body.Replace("\r", "").Split('\n');
                if (response.IsSuccessStatusCode && lines.Length == 4 && lines[0] == "OK" && lines[1] == ip &&
                    lines[3] is "UPDATED" or "NOCHANGE")
                {
                    state.ConfirmedIp = ip; state.ConfirmedRevision = settings.Revision; state.LastSuccess = Now; state.Outcome = "success";
                    state.Message = lines[3] == "UPDATED" ? "DuckDNS confirmed the new IP address." : "DuckDNS confirmed the hostname already has this IP address.";
                }
                else Fatal(state, body == "KO"
                    ? "DuckDNS rejected the update. Correct and save the hostname or token to resume."
                    : "Unexpected response from DuckDNS. Resolve the provider issue, then save settings to retry.", body != "KO");
            }
            else if (response.IsSuccessStatusCode && code is "good" or "nochg" && ((parts.Length == 2 && parts[1] == ip) || (settings.Provider == "Dynu" && parts.Length == 1)))
            {
                state.ConfirmedIp = ip; state.ConfirmedRevision = settings.Revision;
                state.LastSuccess = Now; state.Outcome = "success";
                state.Message = code == "good" ? $"{provider} confirmed the new IP address." : $"{provider} confirmed the hostname already has this IP address.";
            }
            else
                Fatal(state, code switch {
                    "nohost" => $"This hostname is not available with the supplied {provider} credentials.",
                    "abuse" => $"{provider} blocked this hostname or account. Contact {provider} support before resuming.",
                    "badagent" => $"{provider} rejected this updater. Contact the plugin maintainer before resuming.",
                    "!donator" => $"The {provider} account does not support the requested operation.",
                    "notfqdn" or "numhost" => $"{provider} rejected the hostname. Check its spelling and account settings.",
                    _ => $"Unexpected response from {provider}. Updates paused; check the account before resuming."
                }, code is not ("nohost" or "notfqdn" or "numhost"));
            persist();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Never expose response bodies, request headers, credentials, or exception messages.
            state.Outcome = "error";
            state.Message = "Network request failed or timed out. Will retry at the next allowed check.";
            persist();
        }
    }
    private static void Fatal(UpdateState state, string message, bool manual = true) { state.RequiresManualResume = manual; state.Suspended = true; state.Outcome = "error"; state.Message = message; }
}
