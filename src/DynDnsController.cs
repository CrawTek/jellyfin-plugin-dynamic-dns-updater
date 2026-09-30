using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DynDns;

public sealed class SaveRequest
{
    public string? Provider { get; set; }
    public bool Enabled { get; set; }
    public string Hostname { get; set; } = "";
    public string Username { get; set; } = "";
    public string? UpdaterKey { get; set; }
    public bool Resume { get; set; }
}

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("DynDnsUpdater")]
public sealed class DynDnsController : ControllerBase
{
    private static object View(Plugin p) => new { p.Settings.Provider, p.Settings.Enabled, p.Settings.Hostname, p.Settings.Username, p.HasKey, Status = p.State };
    [HttpGet("Settings")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var p = Plugin.Instance;
        await p.Gate.WaitAsync(ct);
        try { return new JsonResult(View(p)); }
        finally { p.Gate.Release(); }
    }
    [HttpPost("Settings")]
    public async Task<IActionResult> Save([FromBody] SaveRequest request, CancellationToken ct)
    {
        var hostname = request.Hostname.Trim().ToLowerInvariant();
        var username = request.Username.Trim();
        if (username.Length > 254 || username.Contains(':') || username.Any(char.IsControl) ||
            request.UpdaterKey?.Length > 1024 || (request.UpdaterKey?.Any(char.IsControl) ?? false))
            return BadRequest("Enter one valid hostname and valid updater credentials.");
        var p = Plugin.Instance;
        await p.Gate.WaitAsync(ct);
        try
        {
            var provider = request.Provider ?? p.Settings.Provider;
            if (!UpdateEngine.ValidProvider(provider)) return BadRequest("Unsupported DNS provider.");
            hostname = UpdateEngine.NormalizeHostname(provider, hostname);
            if (!UpdateEngine.ValidProviderHostname(provider, hostname) && (request.Enabled || hostname.Length != 0))
                return BadRequest("Enter one valid hostname for the selected provider.");
            var needsUsername = provider is not ("DuckDNS" or "FreeDNS");
            if (!needsUsername) username = "";
            var providerChanged = provider != p.Settings.Provider;
            var newKey = !string.IsNullOrWhiteSpace(request.UpdaterKey);
            if (provider == "FreeDNS" && newKey && !UpdateEngine.ValidFreeDnsKey(request.UpdaterKey!))
                return BadRequest("Enter only the FreeDNS Direct URL key after the question mark, not the full URL or account password.");
            // Require fresh credentials even for a disabled provider switch. Separate files also
            // prevent cross-provider disclosure if saving settings is interrupted after writing a key.
            if (providerChanged && (!newKey || (needsUsername && username.Length == 0)))
                return BadRequest("Enter fresh credentials for the selected provider when switching providers.");
            if (request.Enabled && ((needsUsername && username.Length == 0) || (!newKey && !p.HasKey))) return BadRequest("Enter the required provider credentials.");
            var changed = providerChanged || hostname != p.Settings.Hostname || username != p.Settings.Username || newKey;
            var enabling = request.Enabled && !p.Settings.Enabled;
            if (newKey) p.SaveFile(UpdateEngine.KeyFile(provider), request.UpdaterKey!);
            p.SaveSettings(new Settings { Provider = provider, Enabled = request.Enabled, Hostname = hostname, Username = username,
                Revision = changed ? Guid.NewGuid().ToString("N") : p.Settings.Revision });
            if (providerChanged)
            {
                p.State.ConfirmedIp = ""; p.State.ConfirmedRevision = ""; p.State.LastSuccess = null;
                p.State.Message = p.State.Suspended
                    ? "Provider changed. Resolve the provider issue, then save settings to retry."
                    : "Provider changed. Waiting for the next allowed check.";
            }
            if (request.Enabled && (changed || enabling) && !p.State.Suspended)
            {
                p.State.Outcome = "pending";
                p.State.Message = "Settings saved. Waiting for the next allowed check.";
            }
            if (request.Resume || (request.Enabled && p.State.Suspended)) { p.State.Outcome = "pending"; p.State.Suspended = false; p.State.Message = "Resumed. Waiting for the next allowed check."; }
            if (!request.Enabled) { p.State.Outcome = "disabled"; p.State.Message = "Automatic updates are disabled."; }
            // Saving never clears cooldowns, including provider-directed backoff.
            p.PersistState();
            return new JsonResult(View(p));
        }
        finally { p.Gate.Release(); }
    }
    [HttpPost("Check")]
    public async Task<IActionResult> Check(CancellationToken ct)
    { await Plugin.Instance.Check(ct); return await Get(ct); }
}
