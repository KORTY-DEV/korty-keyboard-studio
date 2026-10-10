using System.Windows.Controls;

namespace Korty.App.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView()
    {
        InitializeComponent();
        // Library may have changed while another page was open.
        Loaded += (_, _) => (DataContext as ViewModels.ProfilesViewModel)?.Reload();
    }
}
