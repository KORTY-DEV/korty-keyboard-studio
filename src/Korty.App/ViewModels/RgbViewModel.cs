using System.Windows.Media;
using Korty.Core.Model;
using Korty.Core.Storage;

namespace Korty.App.ViewModels;

public sealed record PresetItem(string Name, string Description, RgbSettings Settings, Brush Swatch);
public sealed record ModeItem(int Index, string Label, string Name);

public sealed class RgbViewModel : Observable
{
    public static readonly string[] Swatches = { "#FF5A1F", "#FF2D55", "#FFB020", "#FFE066", "#3DDC97", "#00C2FF", "#6AA9FF", "#B38CFF", "#FFFFFF" };
    private readonly AppHost _host;
    private RgbSettings _s;
    private string _blockReason = "";
    private string _dryRun = "";
    private bool _busy;

    public IReadOnlyList<ModeItem> Modes { get; } = RgbModes.Names.Select((n, i) => new ModeItem(i, i.ToString("00"), n)).ToList();
    public string ModeName => $"{_s.Mode:00} · {RgbModes.Name(_s.Mode)}";
    public bool DirectionEnabled => RgbModes.UsesDirection(_s.Mode);
    public System.Collections.ObjectModel.ObservableCollection<string> CustomColors { get; } = new();
    public IReadOnlyList<PresetItem> Presets { get; } = RgbPresets.All.Select(p =>
        new PresetItem(p.Name, p.Description, p.Settings, new SolidColorBrush(Color.FromRgb(p.Settings.Primary.R, p.Settings.Primary.G, p.Settings.Primary.B)))).ToList();
    public RelayCommand<PresetItem> ApplyPresetCommand { get; }
    public RelayCommand EnableControlCommand { get; }
    public bool ShowEnable => !_host.SimulationMode && !_host.ControlEnabled;
    public RelayCommand SaveColorCommand { get; }
    public RelayCommand<string> RemoveColorCommand { get; }
    private double _h, _sat, _val;

    private void SyncHsv(bool keepHue)
    {
        var (h, sa, v) = RgbPreviewEngine.ToHsv(_s.Primary.R, _s.Primary.G, _s.Primary.B, keepHue ? _h : 0);
        _h = h; _sat = sa * 100; _val = v * 100;
    }
    private void SetFromHsv(double h, double sa, double v)
    {
        _h = Math.Clamp(h, 0, 360); _sat = Math.Clamp(sa, 0, 100); _val = Math.Clamp(v, 0, 100);
        var (r, g, b) = RgbPreviewEngine.Hsv(_h, _sat / 100, _val / 100);
        Update(_s with { Primary = new RgbColor(r, g, b) }, nameof(PrimaryHex), fromHsv: true);
        Raise(nameof(Hue)); Raise(nameof(Sat)); Raise(nameof(Val));
    }
    public double Hue { get => _h; set => SetFromHsv(value, _sat, _val); }
    public double Sat { get => _sat; set => SetFromHsv(_h, value, _val); }
    public double Val { get => _val; set => SetFromHsv(_h, _sat, value); }
    public Brush HueBrush => new SolidColorBrush(Color.FromRgb(RgbPreviewEngine.Hsv(_h, 1, 1).Item1, RgbPreviewEngine.Hsv(_h, 1, 1).Item2, RgbPreviewEngine.Hsv(_h, 1, 1).Item3));
    public bool AnimatePreview { get => _host.Profile.Settings.AnimatePreview; set { _host.Profile.Settings.AnimatePreview = value; _host.SaveProfile(); Raise(); } }
    public bool IgnoresColor => _s.Mode is 0 or 6 or 8 && _s.ColorMode == 0;
    public string ColorHint => _s.Mode == 0 ? "Режим «Выкл»: цвет не используется."
        : _s.Mode is 6 or 8 ? "Режим радужный по своей природе: выбранный цвет, вероятно, игнорируется (не подтверждено на железе)."
        : _s.ColorMode > 0 ? "Включён радужный/вариативный цвет: выбранный цвет может не применяться." : "";

    private void SaveColor()
    {
        var hex = _s.Primary.ToHex();
        CustomColors.Remove(hex); CustomColors.Insert(0, hex);
        while (CustomColors.Count > 24) CustomColors.RemoveAt(CustomColors.Count - 1);
        PersistColors();
        _host.Report($"Цвет {hex} сохранён в «Мои цвета»");
    }
    private void PersistColors() { _host.Profile.Settings.CustomColors = CustomColors.ToList(); _host.SaveProfile(); }

    public System.Collections.ObjectModel.ObservableCollection<StepRow> Readiness { get; } = new();
    public bool ShowReadiness => !_host.SimulationMode;
    public RelayCommand<string> SetPrimarySwatch { get; }
    public RelayCommand ShowPacketsCommand { get; }
    public AsyncCommand ApplyCommand { get; }

    public RgbViewModel(AppHost host)
    {
        _host = host;
        _s = host.Profile.Get(host.TargetKey).Rgb;
        SyncHsv(false);
        foreach (var c in host.Profile.Settings.CustomColors) CustomColors.Add(c);
        SaveColorCommand = new RelayCommand(SaveColor);
        EnableControlCommand = new RelayCommand(() => _host.EnableControl());
        ApplyPresetCommand = new RelayCommand<PresetItem>(p => { if (p != null) { Update(p.Settings with { Secondary = _s.Secondary }, nameof(Mode)); RaiseAll(); } });
        RemoveColorCommand = new RelayCommand<string>(c => { if (c != null) { CustomColors.Remove(c); PersistColors(); } });
        SetPrimarySwatch = new RelayCommand<string>(h => { if (h != null) PrimaryHex = h; });
        ShowPacketsCommand = new RelayCommand(() => { DryRunText = PacketFormatter.Describe(_host.BuildRgbPackets(_s)); Refresh(); });
        ApplyCommand = new AsyncCommand(ApplyAsync, () => CanApply, ex => _host.ReportError(ex, "Применение подсветки"));
        host.TargetChanged += () => { _s = host.Profile.Get(host.TargetKey).Rgb; SyncHsv(false); RaiseAll(); };
        host.TopologyUpdated += Refresh;
        host.ModeChanged += Refresh;
        Refresh();
    }

    private void Update(RgbSettings s, string prop, bool fromHsv = false)
    {
        _s = s.Clamped();
        if (!fromHsv) { SyncHsv(true); Raise(nameof(Hue)); Raise(nameof(Sat)); Raise(nameof(Val)); }
        Raise(nameof(HueBrush)); Raise(nameof(ColorHint)); Raise(nameof(IgnoresColor));
        _host.Profile.Get(_host.TargetKey).Rgb = _s;
        _host.SaveProfile();
        Raise(prop);
        Raise(nameof(PreviewColor)); Raise(nameof(PrimaryBrush)); Raise(nameof(Summary)); Raise(nameof(ModeName)); Raise(nameof(DirectionEnabled));
        DryRunText = ""; Refresh();
    }

    private void RaiseAll()
    {
        foreach (var p in new[] { nameof(Mode), nameof(PrimaryHex), nameof(SecondaryHex), nameof(Rainbow), nameof(Brightness), nameof(Speed), nameof(Direction),
                     nameof(PreviewColor), nameof(PrimaryBrush), nameof(Summary), nameof(ModeName), nameof(DirectionEnabled), nameof(Hue), nameof(Sat), nameof(Val), nameof(HueBrush), nameof(ColorHint), nameof(UsbTarget), nameof(WirelessTarget), nameof(TargetTitle) }) Raise(p);
        Refresh();
    }

    public bool UsbTarget { get => _host.TargetKey == StudioProfile.UsbKey; set { if (value) _host.SetTarget(StudioProfile.UsbKey); } }
    public bool WirelessTarget { get => _host.TargetKey == StudioProfile.WirelessKey; set { if (value) _host.SetTarget(StudioProfile.WirelessKey); } }
    public string TargetTitle => UsbTarget ? "USB-C" : "2.4 GHz";

    public int Mode { get => _s.Mode; set => Update(_s with { Mode = value }, nameof(Mode)); }
    public int Brightness { get => _s.Brightness; set => Update(_s with { Brightness = value }, nameof(Brightness)); }
    public int Speed { get => _s.Speed; set => Update(_s with { Speed = value }, nameof(Speed)); }
    public int Direction { get => _s.Direction; set => Update(_s with { Direction = value }, nameof(Direction)); }
    public bool Rainbow { get => _s.ColorMode > 0; set => Update(_s with { ColorMode = value ? 1 : 0 }, nameof(Rainbow)); }
    public string PrimaryHex
    {
        get => _s.Primary.ToHex();
        set { if (RgbColor.TryParse(value, out var c) && c != _s.Primary) Update(_s with { Primary = c }, nameof(PrimaryHex)); else Raise(); }
    }
    public string SecondaryHex
    {
        get => _s.Secondary.ToHex();
        set { if (RgbColor.TryParse(value, out var c) && c != _s.Secondary) Update(_s with { Secondary = c }, nameof(SecondaryHex)); else Raise(); }
    }
    public int Red { get => _s.Primary.R; set => Update(_s with { Primary = _s.Primary with { R = (byte)Math.Clamp(value, 0, 255) } }, nameof(PrimaryHex)); }
    public int Green { get => _s.Primary.G; set => Update(_s with { Primary = _s.Primary with { G = (byte)Math.Clamp(value, 0, 255) } }, nameof(PrimaryHex)); }
    public int Blue { get => _s.Primary.B; set => Update(_s with { Primary = _s.Primary with { B = (byte)Math.Clamp(value, 0, 255) } }, nameof(PrimaryHex)); }

    public Color PreviewColor => Color.FromRgb(_s.Primary.R, _s.Primary.G, _s.Primary.B);
    public Brush PrimaryBrush => new SolidColorBrush(PreviewColor);
    public int PreviewBrightness => _s.Brightness;
    public bool Active => _host.TargetDevice?.IsActionable == true;
    public string Summary => $"{ModeName} · {_s.Primary.ToHex()} · яркость {_s.Brightness}/5 · скорость {_s.Speed}/5";

    public string BlockReason { get => _blockReason; private set { if (Set(ref _blockReason, value)) Raise(nameof(HasBlockReason)); } }
    public bool HasBlockReason => !string.IsNullOrEmpty(_blockReason);
    public string DryRunText { get => _dryRun; private set { if (Set(ref _dryRun, value)) Raise(nameof(HasDryRun)); } }
    public bool HasDryRun => !string.IsNullOrEmpty(_dryRun);
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) ApplyCommand.Refresh(); } }
    public bool CanApply => string.IsNullOrEmpty(BlockReason) && !_busy;
    public string ApplyText => _host.SimulationMode ? "Применить к симулятору" : "Применить к клавиатуре";

    public void Refresh()
    {
        var pre = _host.Commands.Check(_host.BuildRgbPackets(_s), _host.TargetDevice);
        BlockReason = pre.AllAllowed ? "" : "Применение недоступно: " + string.Join("; ", pre.DistinctBlockers);
        Readiness.Clear();
        foreach (var r in Korty.App.Readiness.Evaluate(_host, Korty.Core.Protocol.Stages.Rgb, _host.BuildRgbPackets(_s))) Readiness.Add(r);
        Raise(nameof(ShowReadiness)); Raise(nameof(ShowEnable));
        Raise(nameof(CanApply)); Raise(nameof(Active)); Raise(nameof(PreviewBrightness)); Raise(nameof(ApplyText));
        ApplyCommand.Refresh();
    }

    private async Task ApplyAsync()
    {
        Busy = true;
        try
        {
            var r = await _host.Commands.SendAsync(_host.BuildRgbPackets(_s), _host.TargetDevice, null, CancellationToken.None);
            _host.Report(r.Success ? (_host.SimulationMode ? "Симулятор принял настройки подсветки" : "Подсветка отправлена") : r.Message);
        }
        finally { Busy = false; }
    }
}
