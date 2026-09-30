using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DynDns;

// The regular plugin configuration intentionally contains no credentials.
public sealed class PluginConfiguration : BasePluginConfiguration { }

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public override string Name => "Dynamic DNS Updater";
    public override string Description => "Keeps a Dyn, No-IP, DuckDNS or Dynu hostname current with your public IPv4 address.";
    public override Guid Id => Guid.Parse("87444c87-1082-414f-a07d-5e22c87c16f4");
    public string StorePath { get; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public Settings Settings { get; private set; }
    public UpdateState State { get; private set; }
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public Plugin(IApplicationPaths paths, IXmlSerializer xml) : base(paths, xml)
    {
        Instance = this;
        StorePath = Path.Combine(paths.PluginConfigurationsPath, "dyndns-updater");
        Directory.CreateDirectory(StorePath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(StorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Settings = Load<Settings>("settings.json");
        State = Load<UpdateState>("state.json");
    }
    private T Load<T>(string name) where T : new() => File.Exists(Path.Combine(StorePath, name))
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(StorePath, name))) ?? new T() : new T();
    public bool HasKey => UpdateEngine.ValidProvider(Settings.Provider) && File.Exists(Path.Combine(StorePath, UpdateEngine.KeyFile(Settings.Provider)));
    public string ReadKey() => HasKey ? File.ReadAllText(Path.Combine(StorePath, UpdateEngine.KeyFile(Settings.Provider))) : "";
    public void SaveFile(string name, string contents)
    {
        var path = Path.Combine(StorePath, name);
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var writer = new StreamWriter(new FileStream(temporary, options))) writer.Write(contents);
        File.Move(temporary, path, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public void PersistState() => SaveFile("state.json", JsonSerializer.Serialize(State));
    public void SaveSettings(Settings settings) { Settings = settings; SaveFile("settings.json", JsonSerializer.Serialize(settings)); }
    public async Task Check(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await new UpdateEngine(_http).Run(Settings, ReadKey(), State, PersistState, ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "DynDnsUpdater", EmbeddedResourcePath = "Jellyfin.Plugin.DynDns.settings.html" }];
}

public sealed class DnsTask : IScheduledTask
{
    public string Name => "Dynamic DNS Updater";
    public string Key => "DynDnsUpdaterCheck";
    public string Description => "Detects public IP changes and updates the configured DNS hostname.";
    public string Category => "Dynamic DNS Updater";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = UpdateEngine.CheckInterval.Ticks }];
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    { progress.Report(0); await Plugin.Instance.Check(cancellationToken).ConfigureAwait(false); progress.Report(100); }
}
