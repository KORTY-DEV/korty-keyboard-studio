using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Korty.Core.Model;
using Korty.Core.Protocol;
using Korty.Core.Storage;
using Korty.Core.Tft;
using Korty.Rendering;
using Microsoft.Win32;

namespace Korty.App.ViewModels;

/// <summary>Editable proxy over a <see cref="Layer"/>; every change notifies the editor so the preview re-renders.</summary>
public sealed class LayerItem : Observable
{
    private readonly Action _changed;
    public Layer Model { get; }
    public LayerItem(Layer model, Action changed) { Model = model; _changed = changed; }

    public bool IsImage => Model is ImageLayer;
    public bool IsText => Model is TextLayer;
    public string Icon => IsImage ? "▣" : "T";
    public string Name { get => Model.Name; set { Model.Name = value; Raise(); _changed(); } }
    public bool Visible { get => Model.Visible; set { Model.Visible = value; Raise(); _changed(); } }
    public double X { get => Model.X; set { Model.X = Math.Round(value, 1); Raise(); _changed(); } }
    public double Y { get => Model.Y; set { Model.Y = Math.Round(value, 1); Raise(); _changed(); } }
    public double Opacity { get => Model.Opacity; set { Model.Opacity = Math.Clamp(value, 0, 1); Raise(); _changed(); } }

    public double Scale { get => (Model as ImageLayer)?.Scale ?? 1; set { if (Model is ImageLayer l) { l.Scale = Math.Clamp(value, 0.05, 20); Raise(); _changed(); } } }
    public double Rotation
    {
        get => Model switch { ImageLayer i => i.Rotation, TextLayer t => t.Rotation, _ => 0 };
        set { if (Model is ImageLayer i) i.Rotation = value; else if (Model is TextLayer t) t.Rotation = value; Raise(); _changed(); }
    }
    public ImageFit Fit { get => (Model as ImageLayer)?.Fit ?? ImageFit.Cover; set { if (Model is ImageLayer l) { l.Fit = value; Raise(); _changed(); } } }

    public string Text { get => (Model as TextLayer)?.Text ?? ""; set { if (Model is TextLayer t) { t.Text = value.Length > DisplayComposition.MaxTextLength ? value[..DisplayComposition.MaxTextLength] : value; if (string.IsNullOrWhiteSpace(Model.Name) || Model.Name.StartsWith("Текст")) Model.Name = "Текст: " + (t.Text.Length > 14 ? t.Text[..14] + "…" : t.Text); Raise(); Raise(nameof(Name)); _changed(); } } }
    public string FontFamily { get => (Model as TextLayer)?.FontFamily ?? "Segoe UI"; set { if (Model is TextLayer t && !string.IsNullOrWhiteSpace(value)) { t.FontFamily = value; Raise(); _changed(); } } }
    public double FontSize { get => (Model as TextLayer)?.FontSize ?? 18; set { if (Model is TextLayer t) { t.FontSize = Math.Clamp(Math.Round(value), 6, 128); Raise(); _changed(); } } }
    public bool Bold { get => (Model as TextLayer)?.Bold ?? false; set { if (Model is TextLayer t) { t.Bold = value; Raise(); _changed(); } } }
    public bool Italic { get => (Model as TextLayer)?.Italic ?? false; set { if (Model is TextLayer t) { t.Italic = value; Raise(); _changed(); } } }
    public bool Shadow { get => (Model as TextLayer)?.Shadow ?? false; set { if (Model is TextLayer t) { t.Shadow = value; Raise(); _changed(); } } }
    public TextAlign Align { get => (Model as TextLayer)?.Align ?? TextAlign.Center; set { if (Model is TextLayer t) { t.Align = value; Raise(); _changed(); } } }

    public string ColorHex
    {
        get => (Model as TextLayer)?.Color.ToHex() ?? "#FFFFFF";
        set { if (Model is TextLayer t && RgbColor.TryParse(value, out var c)) { t.Color = c; Raise(); Raise(nameof(ColorBrush)); _changed(); } }
    }
    public Brush ColorBrush => Model is TextLayer t ? new SolidColorBrush(Color.FromRgb(t.Color.R, t.Color.G, t.Color.B)) : Brushes.White;
    public void Touch() { Raise(string.Empty); }
}

public sealed record BackgroundItem(BackgroundKind Kind, string Title);
public sealed record DesignItem(string Id, string Name, string Modified, ImageSource? Thumb, int Layers);

public sealed class ScreenEditorViewModel : Observable
{
    public static readonly string[] Swatches = { "#FFFFFF", "#FF5A1F", "#FFB020", "#3DDC97", "#6AA9FF", "#B38CFF", "#FF5C8A", "#000000" };
    public IReadOnlyList<string> FontNames { get; } = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList();
    public IReadOnlyList<ImageFit> Fits { get; } = Enum.GetValues<ImageFit>();
    public IReadOnlyList<TextAlign> Aligns { get; } = Enum.GetValues<TextAlign>();

    private readonly AppHost _host;
    private DisplayComposition _comp = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _playTimer;
    private readonly Stopwatch _playClock = new();
    private double _playStartOffset;
    private bool _dirty, _renderQueued;
    private LayerItem? _selected;
    private ImageSource? _frame;
    private double _playhead;
    private bool _playing, _deviceView, _dither, _busy;
    private string _name = "";
    private string _uploadInfo = "План загрузки ещё не подготовлен.";
    private string _blockReason = "";
    private string _dryRun = "";
    private double _progress;
    private PreparedUpload? _prepared;
    private string _animInfo = "";

    private sealed record PreparedUpload(BuiltDesign Built, TftUploadPlan Plan);

    /// <summary>Display order: TOP-MOST layer first (the model list is bottom → top).</summary>
    public ObservableCollection<LayerItem> Layers { get; } = new();
    public ObservableCollection<DesignItem> Library { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<StepRow> Readiness { get; } = new();
    public bool ShowReadiness => !_host.SimulationMode && _prepared != null;

    public RelayCommand NewCommand { get; }
    public RelayCommand AddImageCommand { get; }
    public RelayCommand AddExampleCommand { get; }
    public RelayCommand AddTextCommand { get; }
    public RelayCommand DeleteLayerCommand { get; }
    public RelayCommand DuplicateLayerCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand CenterCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExportPngCommand { get; }
    public RelayCommand ExportPayloadCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand<DesignItem> OpenDesignCommand { get; }
    public RelayCommand<DesignItem> DuplicateDesignCommand { get; }
    public RelayCommand<DesignItem> DeleteDesignCommand { get; }
    public RelayCommand<string> AssignCommand { get; }
    public RelayCommand<string> SetSwatchCommand { get; }
    public RelayCommand PrepareCommand { get; }
    public AsyncCommand UploadCommand { get; }
    public RelayCommand AddTimeCommand { get; }
    public IReadOnlyList<BackgroundItem> BackgroundItems { get; } = Enum.GetValues<BackgroundKind>().Select(k => new BackgroundItem(k, BackgroundGenerator.Title(k))).ToList();
    private BackgroundItem _bg = null!;
    private string _bgColor1 = "", _bgColor2 = "";
    public BackgroundItem SelectedBackground
    {
        get => _bg;
        set { if (!Set(ref _bg, value)) return; var (a, b) = BackgroundGenerator.DefaultColors(value.Kind); BgColor1 = a.ToHex(); BgColor2 = b.ToHex(); }
    }
    public string BgColor1 { get => _bgColor1; set => Set(ref _bgColor1, value); }
    public string BgColor2 { get => _bgColor2; set => Set(ref _bgColor2, value); }
    public RelayCommand GenerateBackgroundCommand { get; }
    public RelayCommand EnableControlCommand { get; }
    public bool ShowEnable => !_host.SimulationMode && !_host.ControlEnabled;

    private void GenerateBackground()
    {
        if (!RgbColor.TryParse(BgColor1, out var a) || !RgbColor.TryParse(BgColor2, out var b)) { _host.Report("Цвета фона: введите #RRGGBB"); return; }
        var dir = Path.Combine(_host.DataDir, "generated"); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"bg-{Guid.NewGuid():N}.gif");
        try
        {
            GifExport.Save(BackgroundGenerator.Generate(_bg.Kind, a, b), file);
            AddImageFromFile(file);
            if (Selected != null) Selected.Name = _bg.Title;
            _host.Report($"Создан анимированный фон «{_bg.Title}» (12 кадров). Теперь «Подготовить план» → «Отправить на экран».");
        }
        catch (Exception ex) { _host.ReportError(ex, "Создание фона"); }
        finally { try { File.Delete(file); } catch { /* temp file */ } }
    }

    public ScreenEditorViewModel(AppHost host)
    {
        _host = host;
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); _renderQueued = false; RenderNow(); };
        _playTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _playTimer.Tick += (_, _) => Tick();

        NewCommand = new RelayCommand(() => { if (ConfirmDiscard()) LoadComposition(new DisplayComposition()); });
        AddImageCommand = new RelayCommand(AddImage);
        AddExampleCommand = new RelayCommand(AddExample);
        AddTextCommand = new RelayCommand(AddText);
        DeleteLayerCommand = new RelayCommand(DeleteLayer, () => Selected != null);
        DuplicateLayerCommand = new RelayCommand(DuplicateLayer, () => Selected != null);
        MoveUpCommand = new RelayCommand(() => Move(+1), () => Selected != null);
        MoveDownCommand = new RelayCommand(() => Move(-1), () => Selected != null);
        CenterCommand = new RelayCommand(() => { if (Selected != null) { Selected.X = 0; Selected.Y = 0; } }, () => Selected != null);
        SaveCommand = new RelayCommand(Save);
        ExportPngCommand = new RelayCommand(ExportPng);
        ExportPayloadCommand = new RelayCommand(ExportPayload);
        PlayCommand = new RelayCommand(TogglePlay, () => IsAnimated);
        OpenDesignCommand = new RelayCommand<DesignItem>(d => { if (d != null && ConfirmDiscard()) OpenDesign(d.Id); });
        DuplicateDesignCommand = new RelayCommand<DesignItem>(d => { if (d != null) DuplicateDesign(d.Id); });
        DeleteDesignCommand = new RelayCommand<DesignItem>(d => { if (d != null) DeleteDesign(d); });
        AssignCommand = new RelayCommand<string>(key => { if (key != null) Assign(key); });
        SetSwatchCommand = new RelayCommand<string>(hex => { if (hex != null && Selected is { IsText: true }) Selected.ColorHex = hex; });
        PrepareCommand = new RelayCommand(Prepare);
        AddTimeCommand = new RelayCommand(() => AddLayer(new TextLayer { Name = "Время (снимок)", Text = "{time}", FontSize = 26, Y = -44 }));
        SelectedBackground = BackgroundItems[0];
        GenerateBackgroundCommand = new RelayCommand(GenerateBackground);
        EnableControlCommand = new RelayCommand(() => _host.EnableControl());
        host.ModeChanged += () => Raise(nameof(ShowEnable));
        UploadCommand = new AsyncCommand(UploadAsync, () => CanUpload, ex => _host.ReportError(ex, "Отправка на экран"));

        host.TargetChanged += () => { InvalidatePrepared(); Raise(nameof(TargetKey)); Raise(nameof(TargetTitle)); };
        host.TopologyUpdated += () => { if (_prepared != null) RefreshPreflight(); };
        host.ModeChanged += () => { InvalidatePrepared(); };
        ReloadLibrary();
        var last = host.Profile.Settings.LastDesignId;
        if (last != null && LibraryPaths.IsValidId(last) && TryLoad(last)) { /* restored last session */ }
        else LoadComposition(NewDefault());
    }

    private static DisplayComposition NewDefault()
    {
        var c = new DisplayComposition { Name = "Новое оформление" };
        c.Layers.Add(new TextLayer { Name = "Текст: KORTY", Text = "KORTY", FontSize = 30 });
        return c;
    }

    // -------------------------------------------------------- state

    public string TargetKey => _host.TargetKey;
    public string TargetTitle => _host.TargetKey == StudioProfile.UsbKey ? "USB-C" : "2.4 GHz";
    public bool UsbTarget { get => _host.TargetKey == StudioProfile.UsbKey; set { if (value) _host.SetTarget(StudioProfile.UsbKey); } }
    public bool WirelessTarget { get => _host.TargetKey == StudioProfile.WirelessKey; set { if (value) _host.SetTarget(StudioProfile.WirelessKey); } }

    public string Name { get => _name; set { if (Set(ref _name, value)) { _comp.Name = value; MarkDirty(); } } }
    public bool IsDirty { get => _dirty; private set { if (Set(ref _dirty, value)) { Raise(nameof(Title)); Raise(nameof(SaveState)); } } }
    public string Title => (IsDirty ? "● " : "") + (string.IsNullOrWhiteSpace(_name) ? "Без названия" : _name);
    public string SaveState => IsDirty ? "● есть несохранённые изменения" : "✓ сохранено в библиотеке";
    public ImageSource? Frame { get => _frame; private set => Set(ref _frame, value); }
    public string AnimInfo { get => _animInfo; private set => Set(ref _animInfo, value); }

    public LayerItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection)); Raise(nameof(SelectedIsImage)); Raise(nameof(SelectedIsText));
            Raise(nameof(SelectionBounds));
            RefreshCommands();
        }
    }
    public bool HasSelection => _selected != null;
    public bool SelectedIsImage => _selected?.IsImage == true;
    public bool SelectedIsText => _selected?.IsText == true;

    public Rect SelectionBounds => _selected == null || !_selected.Visible ? Rect.Empty : CompositionRenderer.Bounds(_selected.Model, Loader);

    public string BackgroundHex
    {
        get => _comp.Background.ToHex();
        set { if (RgbColor.TryParse(value, out var c)) { _comp.Background = c; Raise(); Raise(nameof(BackgroundBrush)); MarkDirty(); } }
    }
    public Brush BackgroundBrush => new SolidColorBrush(Color.FromRgb(_comp.Background.R, _comp.Background.G, _comp.Background.B));
    public int MaxFrames { get => _comp.MaxFrames; set { _comp.MaxFrames = Math.Clamp(value, 1, 255); Raise(); MarkDirty(); } }

    public bool DeviceView { get => _deviceView; set { if (Set(ref _deviceView, value)) QueueRender(); } }
    public bool Dither { get => _dither; set { if (Set(ref _dither, value)) { QueueRender(); InvalidatePrepared(); } } }

    // playback
    public bool IsAnimated => AnimatedImage != null;
    public double Duration => AnimatedImage?.TotalDurationMs ?? 0;
    public double Playhead { get => _playhead; set { if (Set(ref _playhead, value) && !_playing) QueueRender(); } }
    public bool Playing { get => _playing; private set { if (Set(ref _playing, value)) Raise(nameof(PlayGlyph)); } }
    public string PlayGlyph => Playing ? "⏸" : "▶";
    private DecodedImage? AnimatedImage => CompositionRenderer.FindAnimatedLayer(_comp, Loader) is { } l ? Loader(l) : null;

    private DecodedImage? Loader(ImageLayer l) => _host.Assets.Load(_comp.Id, l);

    // upload
    public string UploadInfo { get => _uploadInfo; private set => Set(ref _uploadInfo, value); }
    public string BlockReason { get => _blockReason; private set { if (Set(ref _blockReason, value)) Raise(nameof(HasBlockReason)); } }
    public bool HasBlockReason => !string.IsNullOrEmpty(_blockReason);
    public string DryRunText { get => _dryRun; private set { if (Set(ref _dryRun, value)) Raise(nameof(HasDryRun)); } }
    public bool HasDryRun => !string.IsNullOrEmpty(_dryRun);
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) UploadCommand.Refresh(); } }
    public bool CanUpload => _prepared != null && !_busy && string.IsNullOrEmpty(BlockReason);
    public string UploadButtonText => _host.SimulationMode ? "Отправить в симулятор" : "Отправить на экран";

    // -------------------------------------------------------- composition lifecycle

    private void LoadComposition(DisplayComposition c)
    {
        StopPlay();
        _comp = c; _comp.Sanitize();
        Layers.Clear();
        foreach (var l in Enumerable.Reverse(_comp.Layers)) Layers.Add(new LayerItem(l, OnLayerChanged));
        _name = _comp.Name;
        Raise(nameof(Name)); Raise(nameof(BackgroundHex)); Raise(nameof(BackgroundBrush)); Raise(nameof(MaxFrames));
        Selected = Layers.FirstOrDefault();
        IsDirty = false;
        Playhead = 0;
        InvalidatePrepared();
        Raise(nameof(IsAnimated)); Raise(nameof(Duration)); RefreshCommands();
        RenderNow();
    }

    private bool TryLoad(string id)
    {
        try { LoadComposition(_host.Library.Load(id)); return true; }
        catch (Exception ex) { _host.Log.Warn("editor", $"Оформление не открыто: {ex.Message}"); return false; }
    }

    private void OpenDesign(string id)
    {
        if (TryLoad(id)) _host.Report($"Открыто оформление «{_comp.Name}»");
        else _host.Report("Не удалось открыть оформление (файл повреждён) — см. журнал.");
    }

    private bool ConfirmDiscard() => !IsDirty ||
        MessageBox.Show("Несохранённые изменения будут потеряны. Продолжить?", "KORTY Keyboard Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void OnLayerChanged() { MarkDirty(); Raise(nameof(SelectionBounds)); Raise(nameof(IsAnimated)); Raise(nameof(Duration)); PlayCommand.Refresh(); }

    private void MarkDirty() { IsDirty = true; InvalidatePrepared(); QueueRender(); }

    private void RefreshCommands()
    {
        foreach (var c in new[] { DeleteLayerCommand, DuplicateLayerCommand, MoveUpCommand, MoveDownCommand, CenterCommand, PlayCommand }) c.Refresh();
    }

    // -------------------------------------------------------- layers

    /// <summary>Loads the bundled 24-frame KORTY animation so new users have something to test with.</summary>
    private void AddExample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "examples", "korty-24frames.gif");
        if (!File.Exists(path)) { _host.Report("Пример не найден: " + path); return; }
        try { AddImageFromFile(path); _host.Report("Добавлен пример: анимация KORTY, 24 кадра"); }
        catch (Exception ex) { _host.ReportError(ex, "Пример анимации"); }
    }

    private void AddImage()
    {
        var dlg = new OpenFileDialog { Title = "Добавить изображение или GIF", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp|Все файлы|*.*", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        try { AddImageFromFile(dlg.FileName); }
        catch (Exception ex) { _host.ReportError(ex, "Не удалось добавить изображение"); MessageBox.Show(ex.Message, "Изображение не добавлено", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Validates by decoding, copies into the library and adds a layer. Throws on unreadable/oversized/unsupported files.</summary>
    public void AddImageFromFile(string path)
    {
        var test = ImageDecoder.Decode(path);
        var asset = _host.Library.ImportAsset(_comp.Id, path);
        var layer = new ImageLayer { Name = Path.GetFileNameWithoutExtension(path), Asset = asset, Fit = ImageFit.Cover };
        AddLayer(layer, bottom: true);
        _host.Report($"Добавлено: {layer.Name} ({test.Width}×{test.Height}{(test.IsAnimated ? $", {test.Frames.Count} кадров, {test.TotalDurationMs} мс" : "")})");
    }

    private void AddText()
    {
        if (_comp.Layers.Count >= DisplayComposition.MaxLayers) { _host.Report($"Максимум слоёв: {DisplayComposition.MaxLayers}"); return; }
        AddLayer(new TextLayer { Name = "Текст: Новый", Text = "Текст", FontSize = 22 });
    }

    private void AddLayer(Layer l, bool bottom = false)
    {
        if (_comp.Layers.Count >= DisplayComposition.MaxLayers) { _host.Report($"Максимум слоёв: {DisplayComposition.MaxLayers}"); return; }
        var item = new LayerItem(l, OnLayerChanged);
        if (bottom) { _comp.Layers.Insert(0, l); Layers.Add(item); }      // pictures/GIFs become the background, text stays on top
        else { _comp.Layers.Add(l); Layers.Insert(0, item); }
        Selected = item;
        MarkDirty();
        Raise(nameof(IsAnimated)); Raise(nameof(Duration)); PlayCommand.Refresh();
    }

    private void DeleteLayer()
    {
        if (Selected == null) return;
        int i = Layers.IndexOf(Selected);
        _comp.Layers.Remove(Selected.Model);
        Layers.Remove(Selected);
        Selected = Layers.Count == 0 ? null : Layers[Math.Clamp(i, 0, Layers.Count - 1)];
        MarkDirty(); Raise(nameof(IsAnimated)); Raise(nameof(Duration)); PlayCommand.Refresh();
    }

    private void DuplicateLayer()
    {
        if (Selected == null) return;
        Layer copy = Selected.Model switch
        {
            ImageLayer i => new ImageLayer { Name = i.Name + " ", Asset = i.Asset, Fit = i.Fit, Scale = i.Scale, Rotation = i.Rotation, X = i.X + 6, Y = i.Y + 6, Opacity = i.Opacity, Visible = i.Visible },
            TextLayer t => new TextLayer { Name = t.Name, Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, Bold = t.Bold, Italic = t.Italic, Color = t.Color, Align = t.Align, Shadow = t.Shadow, Rotation = t.Rotation, X = t.X + 6, Y = t.Y + 6, Opacity = t.Opacity, Visible = t.Visible },
            _ => throw new InvalidOperationException(),
        };
        AddLayer(copy);
    }

    /// <summary>+1 = towards the top of the stack (drawn later, listed earlier).</summary>
    private void Move(int dir)
    {
        if (Selected == null) return;
        int ci = _comp.Layers.IndexOf(Selected.Model), cj = ci + dir;
        if (ci < 0 || cj < 0 || cj >= _comp.Layers.Count) return;
        var sel = Selected;
        (_comp.Layers[ci], _comp.Layers[cj]) = (_comp.Layers[cj], _comp.Layers[ci]);
        int di = Layers.IndexOf(sel);
        Layers.Move(di, di - dir);
        Selected = sel;
        MarkDirty();
    }

    // -------------------------------------------------------- canvas interaction (called from the view)

    public void SelectAt(Point p)
    {
        foreach (var l in Layers) // top-most first
        {
            if (!l.Visible) continue;
            var b = CompositionRenderer.Bounds(l.Model, Loader);
            if (!b.IsEmpty && b.Contains(p)) { Selected = l; return; }
        }
    }

    public void DragBy(double dx, double dy)
    {
        if (Selected == null) return;
        Selected.X += dx; Selected.Y += dy;
    }

    public void WheelZoom(int delta)
    {
        if (Selected == null) return;
        double f = delta > 0 ? 1.06 : 1 / 1.06;
        if (Selected.IsImage) Selected.Scale *= f;
        else if (Selected.IsText) Selected.FontSize = Selected.FontSize * f + (delta > 0 ? 0.5 : -0.5);
    }

    // -------------------------------------------------------- rendering & playback

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true; _renderTimer.Start();
    }

    private void RenderNow()
    {
        try
        {
            var bgra = CompositionRenderer.RenderFrame(_comp, Loader, _playhead);
            if (_deviceView) bgra = Rgb565.ToBgra(Rgb565.FromBgra(bgra, _dither));
            Frame = ImageUtil.FromBgra(bgra);
            var anim = AnimatedImage;
            AnimInfo = anim == null ? "Статичное изображение"
                : $"GIF · кадр {anim.FrameIndexAt(_playhead) + 1}/{anim.Frames.Count} · {anim.TotalDurationMs} мс · лимит на экран {_comp.MaxFrames}";
        }
        catch (Exception ex) { _host.ReportError(ex, "Ошибка предпросмотра"); }
    }

    private void TogglePlay()
    {
        if (Playing) { StopPlay(); return; }
        if (!IsAnimated) return;
        _playStartOffset = _playhead; _playClock.Restart(); _playTimer.Start(); Playing = true;
    }

    private void StopPlay() { _playTimer.Stop(); _playClock.Stop(); Playing = false; }

    private void Tick()
    {
        double d = Duration;
        if (d <= 0) { StopPlay(); return; }
        _playhead = (_playStartOffset + _playClock.Elapsed.TotalMilliseconds) % d;
        Raise(nameof(Playhead));
        RenderNow();
    }

    // -------------------------------------------------------- library

    public void ReloadLibrary()
    {
        Library.Clear();
        foreach (var d in _host.Library.List())
            Library.Add(new DesignItem(d.Id, d.Name, d.Modified.LocalDateTime.ToString("dd.MM.yy HH:mm"),
                d.ThumbnailPath != null ? ImageUtil.LoadFile(d.ThumbnailPath) : null, d.LayerCount));
        Raise(nameof(LibraryEmpty));
    }
    public bool LibraryEmpty => Library.Count == 0;

    private void Save()
    {
        try
        {
            _comp.Name = string.IsNullOrWhiteSpace(_name) ? "Без названия" : _name.Trim();
            var thumb = CompositionRenderer.EncodePng(CompositionRenderer.RenderFrame(_comp, Loader, 0));
            _host.Library.Save(_comp, thumb);
            _host.Profile.Settings.LastDesignId = _comp.Id;
            _host.SaveProfile();
            IsDirty = false;
            ReloadLibrary();
            _host.Report($"Сохранено в библиотеку: «{_comp.Name}»");
        }
        catch (Exception ex) { _host.ReportError(ex, "Не удалось сохранить оформление"); }
    }

    private void DuplicateDesign(string id)
    {
        try { var copy = _host.Library.Duplicate(id); ReloadLibrary(); _host.Report($"Создана копия «{copy.Name}»"); }
        catch (Exception ex) { _host.ReportError(ex, "Копирование не удалось"); }
    }

    private void DeleteDesign(DesignItem d)
    {
        if (MessageBox.Show($"Удалить оформление «{d.Name}» из библиотеки вместе с изображениями?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _host.Library.Delete(d.Id);
            _host.Assets.Forget(d.Id);
            foreach (var cp in _host.Profile.Connections.Values.Where(c => c.DesignId == d.Id)) cp.DesignId = null;
            if (_host.Profile.Settings.LastDesignId == d.Id) _host.Profile.Settings.LastDesignId = null;
            _host.SaveProfile();
            ReloadLibrary();
            if (_comp.Id == d.Id) LoadComposition(NewDefault());
            _host.Report($"Удалено: «{d.Name}»");
        }
        catch (Exception ex) { _host.ReportError(ex, "Удаление не удалось"); }
    }

    private void Assign(string key)
    {
        if (IsDirty || !_host.Library.List().Any(x => x.Id == _comp.Id)) Save();
        _host.Profile.Get(key).DesignId = _comp.Id;
        _host.SaveProfile();
        _host.Report($"«{_comp.Name}» назначено профилю {(key == StudioProfile.UsbKey ? "USB-C" : "2.4 GHz")} (локально; на экран не отправлено)");
    }

    // -------------------------------------------------------- export

    private void ExportPng()
    {
        var dlg = new SaveFileDialog { Filter = "PNG|*.png", FileName = SafeName() + ".png" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bgra = CompositionRenderer.RenderFrame(_comp, Loader, _playhead);
            File.WriteAllBytes(dlg.FileName, CompositionRenderer.EncodePng(bgra));
            _host.Report("Экспорт PNG: " + Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex) { _host.ReportError(ex, "Экспорт PNG"); }
    }

    private void ExportPayload()
    {
        var dlg = new SaveFileDialog { Filter = "Бинарные данные|*.bin", FileName = SafeName() + $".tft-{_host.Family}.bin" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var built = _host.BuildDesign(_comp);
            var payload = TftPayloadBuilder.Build(built.Animation, _host.EncodingOptions(_dither));
            File.WriteAllBytes(dlg.FileName, payload);
            _host.Report($"Payload сохранён в файл ({payload.Length:N0} Б) — для анализа; в клавиатуру не отправлялся");
        }
        catch (Exception ex) { _host.ReportError(ex, "Экспорт payload"); }
    }

    private string SafeName()
    {
        var n = string.IsNullOrWhiteSpace(_comp.Name) ? "design" : _comp.Name;
        foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
        return n;
    }

    // -------------------------------------------------------- upload to screen (strictly through the gate)

    private void InvalidatePrepared()
    {
        _prepared = null;
        Readiness.Clear(); Raise(nameof(ShowReadiness));
        UploadInfo = "План загрузки не подготовлен (оформление изменилось).";
        BlockReason = ""; DryRunText = ""; Progress = 0;
        Raise(nameof(CanUpload)); Raise(nameof(UploadButtonText)); UploadCommand.Refresh();
    }

    private void Prepare()
    {
        try
        {
            var built = _host.BuildDesign(_comp);
            var plan = TftUploadPlanner.Plan(built.Animation, _host.EncodingOptions(_dither));
            _prepared = new PreparedUpload(built, plan);
            Warnings.Clear();
            foreach (var w in built.Warnings.Concat(plan.Warnings)) Warnings.Add(w);
            double kib = plan.TotalWireBytes / 1024.0;
            UploadInfo = $"{plan.FrameCount} кадр(ов) · {kib:N0} КиБ · {plan.Packets.Count} пакет(ов) · {plan.Family}" + (plan.Stage != null ? $" · этап {Korty.Core.Safety.WriteGate.StageTitle(plan.Stage)}" : "");
            DryRunText = PacketFormatter.Describe(plan.Packets);
            RefreshPreflight();
        }
        catch (Exception ex) { _host.ReportError(ex, "Подготовка загрузки"); }
    }

    private void RefreshPreflight()
    {
        if (_prepared == null) return;
        Readiness.Clear();
        foreach (var r in Korty.App.Readiness.Evaluate(_host, _prepared.Plan.Stage ?? Korty.Core.Protocol.Stages.Png, _prepared.Plan.Packets)) Readiness.Add(r);
        Raise(nameof(ShowReadiness));
        var pre = _host.Commands.Check(_prepared.Plan.Packets, _host.TargetDevice);
        BlockReason = pre.AllAllowed ? "" : "Отправка недоступна: " + string.Join("; ", pre.DistinctBlockers);
        Raise(nameof(CanUpload)); UploadCommand.Refresh();
    }

    private async Task UploadAsync()
    {
        if (_prepared == null) return;
        Busy = true; Progress = 0;
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            var r = await _host.Commands.SendAsync(_prepared.Plan.Packets, _host.TargetDevice, progress, CancellationToken.None);
            _host.Report(r.Success ? (_host.SimulationMode ? $"Симулятор принял оформление: {r.Sent} пакетов" : $"Отправлено: {r.Sent} пакетов") : r.Message);
            if (!r.Success) BlockReason = r.Message;
        }
        finally { Busy = false; }
    }
}
