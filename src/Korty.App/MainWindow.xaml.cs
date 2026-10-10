using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Korty.App;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow() { InitializeComponent(); }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            // Dark title bar (Windows 10 20H1+/11). Purely cosmetic; failures are ignored.
            int on = 1;
            var h = new WindowInteropHelper(this).Handle;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
        }
        catch { /* cosmetic */ }
    }
}
