using System.Windows;
using System.Windows.Controls;
using Korty.App.ViewModels;

namespace Korty.App.Views;

public static class RgbSwatches
{
    public static IReadOnlyList<string> All => RgbViewModel.Swatches;
}

public partial class RgbView : UserControl
{
    private RgbViewModel? _vm;

    public RgbView()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += (_, _) => Focus();
        MouseDown += (_, _) => { if (!IsKeyboardFocusWithin) Focus(); };
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as RgbViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    // The chip group does not rebind itself when the edited profile changes (USB ↔ 2.4 GHz), so mirror Mode manually.
    private void OnVmChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RgbViewModel.Mode) || _vm == null) return;
        foreach (var rb in FindChips(this)) rb.IsChecked = rb.Tag is int m && m == _vm.Mode;
    }

    private static IEnumerable<RadioButton> FindChips(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is RadioButton { GroupName: "rgbMode" } rb) yield return rb;
            foreach (var d in FindChips(c)) yield return d;
        }
    }

    private void OnRemoveColor(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is RgbViewModel vm && sender is Button { Tag: string hex }) vm.RemoveColorCommand.Execute(hex);
    }

    // Physical typing drives the reactive preview modes (only when focus is not in a text box).
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.OriginalSource is TextBox || e.IsRepeat) return;
        var k = e.Key;
        string? label = k >= System.Windows.Input.Key.A && k <= System.Windows.Input.Key.Z ? k.ToString()
            : k >= System.Windows.Input.Key.D0 && k <= System.Windows.Input.Key.D9 ? ((int)k - (int)System.Windows.Input.Key.D0).ToString()
            : k == System.Windows.Input.Key.Space ? "Space" : k == System.Windows.Input.Key.Tab ? "Tab" : k == System.Windows.Input.Key.Enter ? "Enter" : null;
        if (label != null) Kb.Press(label);
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (DataContext is RgbViewModel vm && sender is Button { Tag: PresetItem p }) vm.ApplyPresetCommand.Execute(p);
    }

    private void OnSwatch(object sender, RoutedEventArgs e)
    {
        if (DataContext is RgbViewModel vm && sender is Button { Tag: string hex }) vm.PrimaryHex = hex;
    }

    // Mode chips: one RadioButton per mode; the checked chip mirrors the ViewModel.
    private void OnModeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int mode } rb && DataContext is RgbViewModel vm) rb.IsChecked = vm.Mode == mode;
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int mode } && DataContext is RgbViewModel vm && vm.Mode != mode) vm.Mode = mode;
    }
}
