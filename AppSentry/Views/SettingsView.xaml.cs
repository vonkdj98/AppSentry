using System.Windows.Controls;
using AppSentry.Services;

namespace AppSentry.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        AboutLogo.Source = BrandIcon.Render(96);
    }
}
