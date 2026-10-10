using System.IO;
using System.Windows;
using System.Windows.Threading;
using Korty.Core.Diagnostics;
using Korty.Core.Discovery;
using Korty.Core.Hid;
using Korty.Core.Model;
using Korty.Core.Protocol;
using Korty.Core.Recovery;
using Korty.Core.Safety;
using Korty.Core.Simulation;
using Korty.Core.Storage;
using Korty.Core.Tft;
using Korty.Hid.Windows;
using Korty.Rendering;

namespace Korty.App;

/// <summary>Switchable so simulation mode can be toggled at runtime without rebuilding the monitor/service graph.</summary>
public sealed class RoutingEnumerator : IHidEnumerator
{
    public IHidEnumerator Target { get; set; }
    public RoutingEnumerator(IHidEnumerator initial) { Target = initial; }
    public IReadOnlyList<HidInterfaceInfo> Enumerate() => Target.Enumerate();
}

public sealed class RoutingTransport : IHidTransport
{
    public IHidTransport Target { get; set; }
    public RoutingTransport(IHidTransport initial) { Target = initial; }
    public Task<TransportResult> SendAsync(ApprovedWrite write, CancellationToken ct) => Target.SendAsync(write, ct);
}

/// <summary>Composition root + shared application state. All device I/O goes through <see cref="Commands"/>.</summary>
public sealed class AppHost : IDisposable
{
    public const string Version = "1.2.0";
    public string DataDir { get; }
    public DiagLog Log { get; }
    public ProfileStore Store { get; }
    public StudioProfile Profile { get; }
    public DesignLibrary Library { get; }
    public WriteGate Gate { get; }
    public SimulatedKeyboard Sim { get; }
    public DeviceCommandService Commands { get; }
    public RestorePlanner Planner { get; }
    public ConnectionMonitor Monitor { get; }
    public AssetCache Assets { get; }
    public string VerifiedRegistryPath { get; }

    private readonly RoutingEnumerator _enumerator;
    private readonly RoutingTransport _transport;
    private readonly IHidEnumerator? _realEnumerator;
    private readonly WindowsHidTransport? _realTransport;
    private readonly Dispatcher _ui;

    public DeviceTopology Topology { get; private set; } = DeviceTopology.Empty;
    public string? StartupWarning { get; }
    public string TargetKey { get; private set; } = StudioProfile.UsbKey;

    public event Action? TopologyUpdated;
    public event Action? ModeChanged;
    public event Action? TargetChanged;
    public event Action<ConnectionChange>? ConnectionEvent;
    public event Action<string>? Status;

    public AppHost(string? dataDir = null, bool? forceSimulation = null)
    {
        _ui = Dispatcher.CurrentDispatcher;
        DataDir = dataDir ?? ProfileStore.DefaultDirectory;
        Directory.CreateDirectory(DataDir);
        Log = new DiagLog(Path.Combine(DataDir, "logs"));
        Store = new ProfileStore(DataDir);
        Profile = Store.Load();
        if (forceSimulation.HasValue) Profile.Settings.SimulationMode = forceSimulation.Value;
        StartupWarning = Store.LastLoadWarning;
        if (StartupWarning != null) Log.Warn("profile", StartupWarning);
        Library = new DesignLibrary(DataDir);
        Assets = new AssetCache(Library, Log);

        VerifiedRegistryPath = Path.Combine(DataDir, "verified-protocols.json");
        var registry = VerifiedProtocolRegistry.Load(VerifiedRegistryPath);
        foreach (var p in registry.LoadProblems) Log.Warn("registry", p);
        Gate = new WriteGate(registry, Log.Gate);
        if (registry.Entries.Count > 0) Gate.Arm();      // consent was already given on a previous run

        Sim = new SimulatedKeyboard(Profile.Settings.SimulatedFamily);
        try { _realEnumerator = new WindowsHidEnumerator(); _realTransport = new WindowsHidTransport(); }
        catch (Exception ex) { Log.Error("startup", "HID layer unavailable: " + ex.Message); }

        _enumerator = new RoutingEnumerator(Profile.Settings.SimulationMode || _realEnumerator == null ? Sim : _realEnumerator);
        _transport = new RoutingTransport(Profile.Settings.SimulationMode || _realTransport == null ? Sim : _realTransport);
        Commands = new DeviceCommandService(Gate, _transport, Log);
        Planner = new RestorePlanner(Commands);

        Monitor = new ConnectionMonitor(_enumerator, TimeSpan.FromSeconds(2));
        Monitor.TopologyChanged += (_, e) => _ui.BeginInvoke(() => OnTopology(e.Topology, e.Changes));
        Sim.Changed += () => _ui.BeginInvoke(() => { _ = Monitor.PollOnceAsync(); TopologyUpdated?.Invoke(); });
        Log.Info("startup", $"KORTY Keyboard Studio {Version}; simulation={SimulationMode}; writes to real hardware: BLOCKED (verified entries: {registry.Entries.Count})");
    }

    public const string ConsentText =
        "KORTY будет отправлять в клавиатуру команды подсветки, картинок и GIF и настроек клавиатуры (время отклика клавиш, сон, отключение клавиш) — по протоколу проверенного веб-конфигуратора и только для AK820 Pro по USB-кабелю (0C45:8009).\n\n" +
        "Сброс, перепрошивка, переназначение клавиш и неизвестные команды заблокированы навсегда. Отключить управление можно в любой момент на странице «Безопасность».\n\nВключить управление клавиатурой?";

    /// <summary>True when the user has switched control on (consent given) — writes to the real keyboard are then possible.</summary>
    public bool ControlEnabled => Gate.Armed && Gate.VerifiedRegistry.Entries.Count > 0;

    /// <summary>One-click consent: shows a single confirmation, then activates all stages and enables writes. No typing required.</summary>
    public bool EnableControl(Func<string, bool>? confirm = null)
    {
        if (ControlEnabled) return true;
        confirm ??= text => System.Windows.MessageBox.Show(text, "Включить управление клавиатурой", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;
        if (!confirm(ConsentText)) return false;
        var reg = ActivationStages.ActivateAll(VerifiedRegistryPath);
        Gate.ReplaceRegistry(reg);
        Gate.Arm();
        Log.Info("stage", $"Пользователь включил управление клавиатурой (записей в реестре: {reg.Entries.Count})");
        ModeChanged?.Invoke();
        return true;
    }

    public void DisableControl()
    {
        Gate.Disarm();
        var reg = ActivationStages.Deactivate(VerifiedRegistryPath, Stages.Rgb);
        Gate.ReplaceRegistry(reg);
        Log.Info("stage", "Управление клавиатурой отключено пользователем");
        ModeChanged?.Invoke();
    }

    /// <summary>Registry path with the user profile folder shown as %LOCALAPPDATA% (no user name in screenshots/logs).</summary>
    public string VerifiedRegistryDisplayPath => VerifiedRegistryPath.Replace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);

    public bool SimulationMode => Profile.Settings.SimulationMode;
    public ProtocolFamily Family => Profile.Settings.SimulatedFamily;
    public int VerifiedEntryCount => Gate.VerifiedRegistry.Entries.Count;

    public void Start() => Monitor.Start();

    /// <summary>Raw descriptor list (read-only) for snapshots.</summary>
    public IReadOnlyList<HidInterfaceInfo> ScanRaw() => _enumerator.Enumerate();

    private void OnTopology(DeviceTopology t, IReadOnlyList<ConnectionChange> changes)
    {
        Topology = t;
        // A cable device exposing FF13 (control) and FF68 (data) is the verified Feature04 revision: pick that family automatically.
        if (!SimulationMode && Family != ProtocolFamily.Feature04 && t.Usb is { } usb &&
            usb.Interfaces.Any(i => i.Info.UsagePage == 0xFF13) && usb.Interfaces.Any(i => i.Info.UsagePage == 0xFF68))
        {
            Log.Info("mode", "Обнаружены FF13+FF68 на 0C45:8009 — выбрано семейство Feature04 (проверенная USB-ревизия)");
            SetFamily(ProtocolFamily.Feature04);
        }
        foreach (var c in changes)
        {
            Log.Info("connection", $"{c.Kind}: {c.Description}");
            ConnectionEvent?.Invoke(c);
        }
        TopologyUpdated?.Invoke();
    }

    public async Task RefreshNowAsync()
    {
        var t = await Monitor.PollOnceAsync();
        Topology = t;
        TopologyUpdated?.Invoke();
        if (Monitor.LastError != null) Log.Warn("discovery", "enumeration error: " + Monitor.LastError);
    }

    public void SetSimulation(bool on)
    {
        Profile.Settings.SimulationMode = on;
        _enumerator.Target = on || _realEnumerator == null ? Sim : _realEnumerator;
        _transport.Target = on || _realTransport == null ? Sim : _realTransport;
        SaveProfile();
        Log.Info("mode", on ? "Режим симуляции ВКЛ: физическая клавиатура не используется" : "Режим симуляции ВЫКЛ: чтение дескрипторов реального оборудования");
        // Force a fresh topology; removal debounce would otherwise delay the switch.
        _ = RefreshNowAsync();
        _ = Task.Run(async () => { await Monitor.PollOnceAsync(); await Monitor.PollOnceAsync(); await _ui.InvokeAsync(() => { Topology = Monitor.Current; TopologyUpdated?.Invoke(); }); });
        ModeChanged?.Invoke();
    }

    public void SetFamily(ProtocolFamily f)
    {
        Profile.Settings.SimulatedFamily = f;
        Sim.SetFamily(f);
        SaveProfile();
        Log.Info("mode", $"Семейство протокола для планов/симуляции: {f}");
        ModeChanged?.Invoke();
    }

    /// <summary>Lets pages refresh after state changes that are not mode changes (e.g. session arming).</summary>
    public void RaiseStateChanged() => ModeChanged?.Invoke();

    public void SetTarget(string key)
    {
        if (TargetKey == key) return;
        TargetKey = key; TargetChanged?.Invoke();
    }

    public PhysicalDevice? DeviceFor(string key) => key == StudioProfile.UsbKey ? Topology.Usb : Topology.Wireless;
    public PhysicalDevice? TargetDevice => DeviceFor(TargetKey);

    public void SaveProfile()
    {
        try { Store.Save(Profile); }
        catch (Exception ex) { Log.Error("profile", "Не удалось сохранить профиль: " + ex.Message); Status?.Invoke("Не удалось сохранить профиль: " + ex.Message); }
    }

    public void Report(string message) { Log.Info("ui", message); Status?.Invoke(message); }
    public void ReportError(Exception ex, string context)
    {
        Log.Error("ui", $"{context}: {ex.Message}");
        Status?.Invoke($"{context}: {ex.Message}");
    }

    // ---------- packet construction (UI never formats bytes) ----------

    public Feature04Options ProtoOptions => new()
    {
        SaveFlag = (byte)Math.Clamp(Profile.Settings.SaveFlag, 0, 1),
        FinishAfterLighting = Profile.Settings.FinishAfterLighting,
        GohvImage = Profile.Settings.GohvImage,
    };

    public IReadOnlyList<ProtocolPacket> BuildRgbPackets(RgbSettings s) => Family == ProtocolFamily.AaFrame
        ? new[] { AaFrameBuilder.SetLedEffect(s) with { Stage = Stages.Rgb } }
        : Feature04Builder.LightingSequence(s, ProtoOptions);

    public TftEncodingOptions EncodingOptions(bool dither = false) => TftEncodingOptions.For(Family) with { Dither = dither, GohvVariant = Profile.Settings.GohvImage };

    // ---------- owner-driven stage activation (persistent) ----------
    public IReadOnlyList<string> ActiveStages => ActivationStages.ActiveStages(Gate.VerifiedRegistry);

    public void ActivateStage(string stage)
    {
        var reg = ActivationStages.Activate(VerifiedRegistryPath, stage);
        Gate.ReplaceRegistry(reg);
        foreach (var p in reg.LoadProblems) Log.Warn("registry", p);
        Log.Info("stage", $"Этап «{WriteGate.StageTitle(stage)}» включён (записей в реестре: {reg.Entries.Count})");
        ModeChanged?.Invoke();
    }

    public void DeactivateStage(string stage)
    {
        var reg = ActivationStages.Deactivate(VerifiedRegistryPath, stage);
        Gate.ReplaceRegistry(reg);
        Log.Info("stage", $"Этап «{WriteGate.StageTitle(stage)}» и последующие деактивированы (записей в реестре: {reg.Entries.Count}).");
        ModeChanged?.Invoke();
    }

    public BuiltDesign BuildDesign(DisplayComposition c) =>
        CompositionRenderer.Build(c, layer => Assets.Load(c.Id, layer));

    /// <summary>Builds the full restore plan for a connection profile (must run on the UI thread: it renders).</summary>
    public RestorePlan BuildRestorePlan(string key)
    {
        var cp = Profile.Get(key);
        var device = DeviceFor(key);
        var rgb = BuildRgbPackets(cp.Rgb);
        IReadOnlyList<ProtocolPacket>? tft = null; string? name = null;
        if (cp.DesignId != null && LibraryPaths.IsValidId(cp.DesignId))
        {
            try
            {
                var comp = Library.Load(cp.DesignId);
                var built = BuildDesign(comp);
                tft = TftUploadPlanner.Plan(built.Animation, EncodingOptions()).Packets;
                name = comp.Name;
            }
            catch (Exception ex) { Log.Warn("restore", $"Оформление {cp.DesignId[..8]} недоступно: {ex.Message}"); }
        }
        return Planner.Plan(key, cp, device, rgb, tft, name);
    }

    public void Dispose()
    {
        Monitor.Dispose();
        _realTransport?.Dispose();
        SaveProfile();
    }
}

/// <summary>Decoded-image cache keyed by file path + timestamp. Failed loads are logged once.</summary>
public sealed class AssetCache
{
    private readonly DesignLibrary _library;
    private readonly DiagLog _log;
    private readonly Dictionary<string, (DateTime stamp, DecodedImage? img)> _cache = new();
    private readonly HashSet<string> _warned = new();

    public AssetCache(DesignLibrary library, DiagLog log) { _library = library; _log = log; }

    public DecodedImage? Load(string designId, ImageLayer layer)
    {
        try
        {
            var path = _library.AssetPath(designId, layer.Asset);
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var hit) && hit.stamp == stamp) return hit.img;
            DecodedImage? img = null;
            try { img = ImageDecoder.Decode(path); }
            catch (Exception ex) { if (_warned.Add(path)) _log.Warn("image", $"Не удалось прочитать ресурс {layer.Asset[..8]}…: {ex.Message}"); }
            _cache[path] = (stamp, img);
            return img;
        }
        catch (ArgumentException) { return null; }
    }

    public void Forget(string designId)
    {
        foreach (var k in _cache.Keys.Where(k => k.Contains(designId)).ToList()) _cache.Remove(k);
    }
}
