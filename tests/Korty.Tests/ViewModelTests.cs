using Korty.App;
using Korty.App.ViewModels;
using Korty.Core.Model;
using Korty.Core.Protocol;
using Korty.Core.Safety;
using Korty.Core.Simulation;
using Korty.Core.Storage;
using Xunit;

namespace Korty.Tests;

/// <summary>Application-layer tests: real AppHost + real ViewModels in simulation mode, on an STA thread, temp data dir.</summary>
public class ViewModelTests
{
    private static void WithHost(Action<AppHost, TempDir> body) => Sta.Run(() =>
    {
        using var dir = new TempDir();
        using var host = new AppHost(dir.Path, forceSimulation: true);
        Refresh(host);   // the app does this on Start(); tests drive polling by hand
        body(host, dir);
    });

    // Helper (not a test method): blocking is intentional — the tests run on a dedicated STA thread without a message pump.
    private static void Refresh(AppHost host) => host.RefreshNowAsync().Wait();

    private static bool WaitFor(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    [Fact]
    public void LayerListIsTopFirstAndMovesAreConsistentWithTheModel() => WithHost((host, dir) =>
    {
        var vm = new ScreenEditorViewModel(host);
        vm.AddTextCommand.Execute(null);                 // new layer goes on top
        vm.AddTextCommand.Execute(null);
        Assert.Equal(3, vm.Layers.Count);
        var topId = vm.Layers[0].Model.Id; var bottom = vm.Layers[2].Model;
        vm.SaveCommand.Execute(null);
        var saved = host.Library.Load(host.Profile.Settings.LastDesignId!);
        Assert.Equal(topId, saved.Layers[^1].Id);        // model list is bottom → top: last == top-most
        Assert.Equal(bottom.Id, saved.Layers[0].Id);

        vm.Selected = vm.Layers[2];                      // bottom
        vm.MoveUpCommand.Execute(null);
        Assert.Equal(bottom.Id, vm.Layers[1].Model.Id);
        vm.MoveUpCommand.Execute(null);
        Assert.Equal(bottom.Id, vm.Layers[0].Model.Id);  // now top-most
        vm.MoveUpCommand.Execute(null);                  // already on top: no-op, no crash
        Assert.Equal(bottom.Id, vm.Layers[0].Model.Id);
        vm.SaveCommand.Execute(null);
        var after = host.Library.Load(host.Profile.Settings.LastDesignId!);
        Assert.Equal(bottom.Id, after.Layers[^1].Id);
    });

    [Fact]
    public void HitTestPicksTopMostLayer() => WithHost((host, dir) =>
    {
        var vm = new ScreenEditorViewModel(host);
        vm.AddTextCommand.Execute(null);
        var upper = vm.Layers[0]; var lower = vm.Layers[1];
        upper.X = lower.X = 0; upper.Y = lower.Y = 0;    // identical bounds
        vm.SelectAt(new System.Windows.Point(64, 64));
        Assert.Same(upper, vm.Selected);
        upper.Visible = false;
        vm.SelectAt(new System.Windows.Point(64, 64));
        Assert.Same(lower, vm.Selected);                 // hidden layers are not hittable
        vm.SelectAt(new System.Windows.Point(2, 2));      // empty area keeps the previous selection
        Assert.Same(lower, vm.Selected);
    });

    [Fact]
    public void DeleteDuplicateAndLimits() => WithHost((host, dir) =>
    {
        var vm = new ScreenEditorViewModel(host);
        vm.Selected = vm.Layers[0];
        vm.DuplicateLayerCommand.Execute(null);
        Assert.Equal(2, vm.Layers.Count);
        vm.DeleteLayerCommand.Execute(null); vm.DeleteLayerCommand.Execute(null);
        Assert.Empty(vm.Layers); Assert.Null(vm.Selected);
        Assert.False(vm.DeleteLayerCommand.CanExecute(null));
        for (int i = 0; i < DisplayComposition.MaxLayers + 5; i++) vm.AddTextCommand.Execute(null);
        Assert.Equal(DisplayComposition.MaxLayers, vm.Layers.Count);
    });

    [Fact]
    public void AnimatedGifIsDetectedPlaysAndRendersDifferentFrames() => WithHost((host, dir) =>
    {
        var gif = Path.Combine(dir.Path, "a.gif");
        File.WriteAllBytes(gif, GifWriter.Build(4, 4,
            new GifWriter.F(0, 0, 4, 4, GifWriter.Fill(16, 1), 10, 1),     // red 100 ms
            new GifWriter.F(0, 0, 4, 4, GifWriter.Fill(16, 2), 10, 1)));   // blue 100 ms
        var vm = new ScreenEditorViewModel(host);
        vm.AddImageFromFile(gif);
        Assert.True(vm.IsAnimated); Assert.Equal(200, vm.Duration);
        vm.SaveCommand.Execute(null);
        var comp = host.Library.Load(host.Profile.Settings.LastDesignId!);
        CompositionRendererSample(host, comp, 10, out var first);
        CompositionRendererSample(host, comp, 150, out var second);
        Assert.True(first.r > 200 && first.b < 50, "frame at 10 ms must be red");
        Assert.True(second.b > 200 && second.r < 50, "frame at 150 ms must be blue");
        Assert.True(vm.PlayCommand.CanExecute(null));
    });

    private static void CompositionRendererSample(AppHost host, DisplayComposition comp, double t, out (byte r, byte g, byte b) color)
    {
        var px = Korty.Rendering.CompositionRenderer.RenderFrame(comp, l => host.Assets.Load(comp.Id, l), t);
        var (r, g, b, _) = Px.At(px, 5, 5);
        color = (r, g, b);
    }

    [Fact]
    public void UnsupportedAndCorruptImagesAreRejectedWithoutChangingTheDesign() => WithHost((host, dir) =>
    {
        var vm = new ScreenEditorViewModel(host);
        int before = vm.Layers.Count;
        var bad = Path.Combine(dir.Path, "bad.png"); File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4 });
        Assert.ThrowsAny<Exception>(() => vm.AddImageFromFile(bad));
        var exe = Path.Combine(dir.Path, "evil.exe"); File.WriteAllBytes(exe, new byte[16]);
        Assert.ThrowsAny<Exception>(() => vm.AddImageFromFile(exe));
        Assert.Equal(before, vm.Layers.Count);
    });

    [Fact]
    public void SimulatedUploadAndRgbReachTheSimulatorOnlyThroughTheGate() => WithHost((host, dir) =>
    {
        var ed = new ScreenEditorViewModel(host);
        ed.PrepareCommand.Execute(null);
        Assert.True(ed.CanUpload, ed.BlockReason);
        ed.UploadCommand.Execute(null);
        Assert.True(WaitFor(() => host.Sim.Screen != null));
        Assert.False(host.Sim.ScreenIsBlack);

        var rgb = new RgbViewModel(host) { Brightness = 5, Mode = 9, PrimaryHex = "#00FF00" };
        Assert.True(rgb.CanApply, rgb.BlockReason);
        rgb.ApplyCommand.Execute(null);
        Assert.True(WaitFor(() => host.Sim.Rgb != null));
        Assert.Equal(9, host.Sim.Rgb!.Mode); Assert.Equal(new RgbColor(0, 255, 0), host.Sim.Rgb.Primary); Assert.Equal(5, host.Sim.Rgb.Brightness);
    });

    [Fact]
    public void UploadIsBlockedWithoutDeviceAndInRealModeEvenIfEverythingElseWorks() => WithHost((host, dir) =>
    {
        host.Sim.SetConnection(SimConnection.None);
        Refresh(host); Refresh(host); Refresh(host);
        var ed = new ScreenEditorViewModel(host);
        ed.PrepareCommand.Execute(null);
        Assert.False(ed.CanUpload);
        Assert.Contains("не подключено", ed.BlockReason);

        // 2.4 GHz target while the simulated receiver exposes no matching control interface.
        host.Sim.SetConnection(SimConnection.Wireless24);
        Refresh(host);
        host.SetTarget(StudioProfile.WirelessKey);
        ed.PrepareCommand.Execute(null);
        Assert.False(ed.CanUpload);
        Assert.Contains("нет vendor-интерфейса", ed.BlockReason);
    });

    [Fact]
    public void RgbSettingsPersistPerConnectionAcrossHostRestarts() => Sta.Run(() =>
    {
        using var dir = new TempDir();
        using (var host = new AppHost(dir.Path, true))
        {
            var rgb = new RgbViewModel(host) { Mode = 7, Speed = 1, PrimaryHex = "#112233" };
            host.SetTarget(StudioProfile.WirelessKey);
            Assert.Equal(1, rgb.Mode == 7 ? 0 : 1);          // wireless profile still has the default mode
            rgb.Mode = 15; rgb.PrimaryHex = "#445566";
        }
        using (var host2 = new AppHost(dir.Path, true))
        {
            Assert.Equal(7, host2.Profile.Get(StudioProfile.UsbKey).Rgb.Mode);
            Assert.Equal(new RgbColor(0x11, 0x22, 0x33), host2.Profile.Get(StudioProfile.UsbKey).Rgb.Primary);
            Assert.Equal(15, host2.Profile.Get(StudioProfile.WirelessKey).Rgb.Mode);
            Assert.Equal(new RgbColor(0x44, 0x55, 0x66), host2.Profile.Get(StudioProfile.WirelessKey).Rgb.Primary);
        }
    });

    [Fact]
    public void InvalidColorInputKeepsPreviousValue() => WithHost((host, dir) =>
    {
        var rgb = new RgbViewModel(host) { PrimaryHex = "#102030" };
        rgb.PrimaryHex = "not a colour"; rgb.PrimaryHex = "#12"; rgb.SecondaryHex = "zzzzzz";
        Assert.Equal("#102030", rgb.PrimaryHex); Assert.Equal("#000000", rgb.SecondaryHex);
    });

    [Fact]
    public void RestorePlanForSimulatedUsbRunsAndForAbsentWirelessIsBlocked() => WithHost((host, dir) =>
    {
        var vm = new ProfilesViewModel(host);
        vm.Usb.BuildPlan();
        Assert.True(vm.Usb.ExecuteCommand.CanExecute(null));
        vm.Usb.ExecuteCommand.Execute(null);
        Assert.True(WaitFor(() => host.Sim.Rgb != null));
        vm.Wireless.BuildPlan();
        Assert.False(vm.Wireless.ExecuteCommand.CanExecute(null));
        vm.Usb.RestoreOnReconnect = true;
        Assert.True(host.Profile.Get(StudioProfile.UsbKey).RestoreOnReconnect);
    });

    [Fact]
    public void ModeToggleSwitchesBetweenSimulatorAndRealEnumeratorWithoutWriting() => Sta.Run(() =>
    {
        using var dir = new TempDir();
        using var host = new AppHost(dir.Path, forceSimulation: false);
        // Real mode: only descriptor reads happen (zero access). The write counter of the simulator stays untouched.
        Refresh(host);
        Assert.Equal(0, host.Sim.AcceptedWrites);
        Assert.False(host.SimulationMode);
        var rgb = new RgbViewModel(host);
        // Even if a real keyboard is attached, nothing is verified: Apply must be blocked.
        Assert.False(rgb.CanApply);
        Assert.False(rgb.ApplyCommand.CanExecute(null));
        host.SetSimulation(true);
        Assert.True(host.SimulationMode);
    });

    [Fact]
    public void CorruptProfileOnStartupIsReportedNotFatal() => Sta.Run(() =>
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "profile.json"), "\0\0 not json");
        using var host = new AppHost(dir.Path, true);
        Assert.NotNull(host.StartupWarning);
        Assert.Equal(StudioProfile.CurrentSchema, host.Profile.SchemaVersion);
        _ = new MainViewModel(host);   // the whole UI graph still constructs
    });

    [Fact]
    public void StageCardsActivateOnlyOnOwnerConfirmationInOrderAndPersist() => Sta.Run(() =>
    {
        using var dir = new TempDir();
        using (var host = new AppHost(dir.Path, forceSimulation: false))
        {
            var rgb = new StageCardViewModel(host, ActivationStages.Infos[0], _ => false);   // owner declines
            rgb.ActionCommand.Execute(null);
            Assert.Empty(host.ActiveStages); Assert.False(File.Exists(host.VerifiedRegistryPath));

            var png = new StageCardViewModel(host, ActivationStages.Infos[1], _ => true);
            Assert.False(png.ActionCommand.CanExecute(null));                                // PNG before RGB: not possible

            new StageCardViewModel(host, ActivationStages.Infos[0], _ => true).ActionCommand.Execute(null);   // owner confirms
            Assert.Equal(new[] { Stages.Rgb }, host.ActiveStages);
            Assert.True(File.Exists(host.VerifiedRegistryPath));
            Assert.False(host.Gate.Armed);                                                   // activation never arms the session
            png.Refresh(); Assert.True(png.ActionCommand.CanExecute(null));
            Assert.Equal(0, host.Sim.AcceptedWrites);                                        // and sends nothing
        }
        using (var again = new AppHost(dir.Path, forceSimulation: false))                    // persists across restarts
        {
            Assert.Equal(new[] { Stages.Rgb }, again.ActiveStages);
            again.DeactivateStage(Stages.Rgb);
            Assert.Empty(again.ActiveStages);
        }
    });

    [Fact]
    public void ReadinessChecklistExplainsWhatBlocksTheFirstRealWrite() => WithHost((host, dir) =>
    {
        var ok = Readiness.Evaluate(host, Stages.Rgb, host.BuildRgbPackets(new RgbSettings()));
        Assert.Equal(3, ok.Count); Assert.All(ok, r => Assert.Equal("✓", r.Mark));           // simulator: everything fine

        host.Sim.SetConnection(SimConnection.Wireless24);                                    // receiver without control interface
        Refresh(host); host.SetTarget(StudioProfile.WirelessKey);
        var rows = Readiness.Evaluate(host, Stages.Rgb, host.BuildRgbPackets(new RgbSettings()));
        Assert.Equal("✕", rows.Single(r => r.Title == "Интерфейсы").Mark);
        Assert.Contains("FF13", rows.Single(r => r.Title == "Интерфейсы").Detail);
    });
}
