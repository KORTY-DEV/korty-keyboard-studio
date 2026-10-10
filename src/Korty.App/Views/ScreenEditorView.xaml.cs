using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Korty.App.ViewModels;

namespace Korty.App.Views;

public partial class ScreenEditorView : UserControl
{
    private const double Zoom = 3.0; // 128 px canvas shown at 384 px
    private Point _last;
    private bool _dragging;
    private ScreenEditorViewModel? _vm;

    public ScreenEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ScreenEditorViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
            UpdateSelection();
        };
    }

    private void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScreenEditorViewModel.SelectionBounds) or nameof(ScreenEditorViewModel.Selected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        if (_vm == null) { SelRect.Visibility = Visibility.Collapsed; return; }
        var b = _vm.SelectionBounds;
        if (b.IsEmpty || double.IsNaN(b.Width)) { SelRect.Visibility = Visibility.Collapsed; return; }
        SelRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelRect, b.X * Zoom); Canvas.SetTop(SelRect, b.Y * Zoom);
        SelRect.Width = Math.Max(2, b.Width * Zoom); SelRect.Height = Math.Max(2, b.Height * Zoom);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var p = e.GetPosition(Surface);
        _vm.SelectAt(new Point(p.X / Zoom, p.Y / Zoom));
        _last = p; _dragging = _vm.HasSelection;
        Surface.CaptureMouse();
        UpdateSelection();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _vm == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Surface);
        _vm.DragBy((p.X - _last.X) / Zoom, (p.Y - _last.Y) / Zoom);
        _last = p;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Surface.ReleaseMouseCapture();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _vm?.WheelZoom(e.Delta);
        e.Handled = true;
    }
}
