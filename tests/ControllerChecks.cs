using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.DynDns;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Mvc;

public static class ControllerChecks
{
    public static async Task Run(Action<bool, string> assert)
    {
        var folder = Path.Combine(Path.GetTempPath(), "dyndns-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var paths = DispatchProxy.Create<IApplicationPaths, TestInterface>();
            ((TestInterface)(object)paths).Folder = folder;
            var xml = DispatchProxy.Create<IXmlSerializer, TestInterface>();
            var plugin = new Plugin(paths, xml);
            var api = new DynDnsController();
            assert(await api.Save(new SaveRequest { Enabled = false }, default) is JsonResult && !plugin.Settings.Enabled, "Unconfigured disabled settings save without hidden required fields");
            plugin.SaveFile("updater.key", "legacy-dyn-key");
            plugin.SaveSettings(new Settings { Enabled = true, Hostname = "old.example.net", Username = "dyn-user", Revision = "old" });
            var request = new SaveRequest { Provider = "NoIP", Hostname = "all.ddnskey.com", Username = "noip-user", Enabled = true };
            assert(await api.Save(request, default) is BadRequestObjectResult, "Provider switch rejects blank credential");
            request.Enabled = false;
            assert(await api.Save(request, default) is BadRequestObjectResult && plugin.Settings.Provider == "Dyn", "Disabled switch cannot inherit Dyn key");
            var deadline = DateTimeOffset.UtcNow.AddHours(2);
            plugin.State.NextAttempt = deadline; plugin.State.Suspended = true;
            plugin.State.LastSuccess = DateTimeOffset.UtcNow; plugin.State.ConfirmedIp = "8.8.8.8";
            request.UpdaterKey = "new-noip-key";
            assert(await api.Save(request, default) is JsonResult, "Switch with fresh No-IP credentials saves");
            assert(plugin.ReadKey() == "new-noip-key" && File.ReadAllText(Path.Combine(plugin.StorePath, "updater.key")) == "legacy-dyn-key", "Separate credential files preserve legacy Dyn key");
            assert(plugin.Settings.Revision != "old" && plugin.State.Suspended && plugin.State.NextAttempt == deadline, "Switch changes revision but preserves suspension and cooldown");
            assert(plugin.State.LastSuccess == null && plugin.State.ConfirmedIp == "", "Provider switch clears previous provider confirmation");
            var saved = (JsonResult)await api.Get(default);
            var json = JsonSerializer.Serialize(saved.Value);
            assert(!json.Contains("new-noip-key") && !json.Contains("legacy-dyn-key") && json.Contains("NoIP"), "Settings API returns provider without secrets");
            request.Provider = null; request.UpdaterKey = null; request.Resume = true;
            assert(await api.Save(request, default) is JsonResult && plugin.Settings.Provider == "NoIP" && plugin.ReadKey() == "new-noip-key", "Save without provider keeps current provider and key");
            assert(!plugin.State.Suspended && plugin.State.NextAttempt == deadline, "Resume cannot bypass cooldown");
            plugin.State.Message = "No-IP confirmed the hostname already has this IP address.";
            plugin.State.LastSuccess = DateTimeOffset.UtcNow;
            var lastSuccess = plugin.State.LastSuccess;
            request.Resume = false;
            var disabledResult = (JsonResult)await api.Save(request, default);
            assert(JsonSerializer.Serialize(disabledResult.Value).Contains("Automatic updates are disabled.") && plugin.State.LastSuccess == lastSuccess && plugin.State.NextAttempt == deadline,
                "Disabling immediately replaces stale success message without losing history or cooldown");
            var reloaded = new Plugin(paths, xml);
            assert(reloaded.State.Outcome == "disabled" && reloaded.State.Message == "Automatic updates are disabled.", "Disabled status survives restart");
            assert(reloaded.Settings.Provider == "NoIP" && reloaded.ReadKey() == "new-noip-key" && reloaded.State.NextAttempt == deadline, "No-IP settings, credential and cooldown survive restart");
            request.Provider = "Dyn";
            assert(await api.Save(request, default) is BadRequestObjectResult, "Switch back also requires fresh credentials");
            request.Provider = "https://attacker.example"; request.UpdaterKey = "unused";
            assert(await api.Save(request, default) is BadRequestObjectResult && reloaded.ReadKey() == "new-noip-key", "Unsupported provider cannot change credentials");
            reloaded.SaveFile("settings.json", "{\"Enabled\":true,\"Hostname\":\"old.example.net\",\"Username\":\"dyn-user\"}");
            var legacy = new Plugin(paths, xml);
            assert(legacy.Settings.Provider == "Dyn" && legacy.ReadKey() == "legacy-dyn-key", "Legacy settings load original Dyn credential");
            var duckRequest = new SaveRequest { Provider = "DuckDNS", Enabled = true, Hostname = "sample" };
            assert(await api.Save(duckRequest, default) is BadRequestObjectResult, "DuckDNS cannot inherit Dyn credential");
            duckRequest.UpdaterKey = "duck-test-token";
            assert(await api.Save(duckRequest, default) is JsonResult && legacy.Settings.Hostname == "sample.duckdns.org" && legacy.Settings.Username == "", "DuckDNS settings accept token without username and normalize hostname");
            assert(legacy.ReadKey() == "duck-test-token" && File.ReadAllText(Path.Combine(legacy.StorePath, "updater.key")) == "legacy-dyn-key" && File.ReadAllText(Path.Combine(legacy.StorePath, "noip-updater.key")) == "new-noip-key", "All three providers have isolated credentials");
            var duckReloaded = new Plugin(paths, xml);
            assert(duckReloaded.ReadKey() == "duck-test-token" && duckReloaded.Settings.Provider == "DuckDNS", "DuckDNS token survives restart");
            assert(!JsonSerializer.Serialize(((JsonResult)await api.Get(default)).Value).Contains("duck-test-token"), "DuckDNS token never returned by settings API");
            assert(duckReloaded.State.Outcome == "pending", "New provider settings wait for confirmation before showing green");
            duckRequest.Hostname = "outside.example.org";
            assert(await api.Save(duckRequest, default) is BadRequestObjectResult, "DuckDNS settings reject other domains");
            duckRequest.Provider = "NoIP"; duckRequest.Hostname = "all.ddnskey.com"; duckRequest.Username = "noip-user"; duckRequest.UpdaterKey = null;
            assert(await api.Save(duckRequest, default) is BadRequestObjectResult && duckReloaded.Settings.Provider == "DuckDNS", "Switching from DuckDNS cannot reuse its token for No-IP");
            var dynuRequest = new SaveRequest { Provider = "Dynu", Enabled = true, Hostname = "sample.dynu.com", Username = "dynu-user" };
            assert(await api.Save(dynuRequest, default) is BadRequestObjectResult, "Dynu cannot inherit DuckDNS token");
            dynuRequest.UpdaterKey = "dynu-test-secret"; dynuRequest.Username = "";
            assert(await api.Save(dynuRequest, default) is BadRequestObjectResult, "Dynu requires a username");
            dynuRequest.Username = "dynu-user";
            assert(await api.Save(dynuRequest, default) is JsonResult && duckReloaded.ReadKey() == "dynu-test-secret", "Dynu settings save with fresh credentials");
            assert(File.ReadAllText(Path.Combine(duckReloaded.StorePath, "duckdns-updater.key")) == "duck-test-token" && File.ReadAllText(Path.Combine(duckReloaded.StorePath, "noip-updater.key")) == "new-noip-key" && File.ReadAllText(Path.Combine(duckReloaded.StorePath, "updater.key")) == "legacy-dyn-key", "Dynu leaves all previous provider secrets untouched");
            var dynuReloaded = new Plugin(paths, xml);
            assert(dynuReloaded.Settings.Provider == "Dynu" && dynuReloaded.ReadKey() == "dynu-test-secret", "Dynu settings survive restart");
            assert(!JsonSerializer.Serialize(((JsonResult)await api.Get(default)).Value).Contains("dynu-test-secret"), "Dynu settings API never returns credential");
            dynuReloaded.State.Suspended = true; dynuReloaded.State.RequiresManualResume = false;
            dynuReloaded.State.NextAttempt = deadline; dynuRequest.UpdaterKey = null;
            await api.Save(dynuRequest, default);
            assert(!dynuReloaded.State.Suspended && dynuReloaded.State.NextAttempt == deadline, "Enabled save retries paused updates without bypassing cooldown");
            dynuReloaded.State.Suspended = true;
            dynuRequest.UpdaterKey = "corrected-secret";
            await api.Save(dynuRequest, default);
            assert(!dynuReloaded.State.Suspended && dynuReloaded.State.NextAttempt == deadline, "Corrected credential auto-resumes while preserving cooldown");
            dynuReloaded.State.Suspended = true; dynuReloaded.State.RequiresManualResume = false;
            dynuRequest.UpdaterKey = null; dynuRequest.Hostname = "corrected.dynu.com";
            await api.Save(dynuRequest, default);
            assert(!dynuReloaded.State.Suspended, "Corrected hostname auto-resumes");
            dynuReloaded.State.Suspended = true; dynuReloaded.State.RequiresManualResume = true;
            dynuRequest.UpdaterKey = "another-secret";
            await api.Save(dynuRequest, default);
            assert(!dynuReloaded.State.Suspended && dynuReloaded.State.NextAttempt == deadline, "Save resumes provider block while preserving cooldown");
            dynuReloaded.State.Suspended = true;
            dynuRequest.Enabled = false; dynuRequest.UpdaterKey = null;
            await api.Save(dynuRequest, default);
            assert(dynuReloaded.State.Suspended, "Disabled save does not resume paused updates");
            dynuRequest.UpdaterKey = null; dynuRequest.Resume = true;
            await api.Save(dynuRequest, default);
            assert(!dynuReloaded.State.Suspended && dynuReloaded.State.NextAttempt == deadline, "Explicit provider retry preserves cooldown");
            var freeRequest = new SaveRequest { Provider = "FreeDNS", Enabled = true, Hostname = "media.example.net" };
            assert(await api.Save(freeRequest, default) is BadRequestObjectResult, "FreeDNS cannot inherit another provider credential");
            freeRequest.UpdaterKey = "https://freedns.afraid.org/dynamic/update.php?fakekey";
            assert(await api.Save(freeRequest, default) is BadRequestObjectResult, "FreeDNS rejects full URL instead of key");
            freeRequest.UpdaterKey = "fakeFreeDnsKey==";
            assert(await api.Save(freeRequest, default) is JsonResult, "FreeDNS saves without username");
            var freeReloaded = new Plugin(paths, xml);
            assert(freeReloaded.Settings.Provider == "FreeDNS" && freeReloaded.ReadKey() == "fakeFreeDnsKey==", "FreeDNS settings survive restart");
            assert(File.ReadAllText(Path.Combine(freeReloaded.StorePath, "dynu-updater.key")) == "another-secret", "FreeDNS preserves previous provider secret");
            assert(!JsonSerializer.Serialize(((JsonResult)await api.Get(default)).Value).Contains("fakeFreeDnsKey"), "FreeDNS API never returns secret");
        }
        finally { Directory.Delete(folder, true); }
    }
}

// Only Jellyfin host interfaces are substituted; controller and credential persistence are real.
public class TestInterface : DispatchProxy
{
    public string Folder { get; set; } = "";
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.ReturnType == typeof(string)) return Folder;
        throw new NotSupportedException(method?.Name);
    }
}
