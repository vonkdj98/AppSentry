using System.Windows;
using System.Windows.Controls;
using AppSentry.ViewModels;

namespace AppSentry.Views;

public partial class ActivityView : UserControl
{
    private ActivityViewModel? _vm;

    public ActivityView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as ActivityViewModel);
        Unloaded += (_, _) => Bind(null);
        Loaded += (_, _) => Bind(DataContext as ActivityViewModel);
    }

    private void Bind(ActivityViewModel? vm)
    {
        if (_vm != null) _vm.ScrollRequested -= OnScrollRequested;
        _vm = vm;
        if (_vm != null) _vm.ScrollRequested += OnScrollRequested;
    }

    private void OnScrollRequested(EventItemViewModel item) =>
        Dispatcher.BeginInvoke(() => EventList.ScrollIntoView(item), System.Windows.Threading.DispatcherPriority.Background);
}
