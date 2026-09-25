using System.Windows;
using System.Windows.Controls;
using AppSentry.Core.Detection;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.Views;

/// <summary>Add/edit one exclusion, with a live preview of which past events it would have matched.</summary>
public partial class ExclusionDialog : Window
{
    private readonly List<(string Name, int Count)> _names;
    private ExclusionEntry? _result;

    private ExclusionDialog(ExclusionEntry entry, IReadOnlyList<ChangeEvent> history, bool isNew)
    {
        InitializeComponent();
        Icon = BrandIcon.Render(32);
        Heading.Text = isNew ? "Add exclusion" : "Edit exclusion";
        _names = history.GroupBy(e => e.App.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        PatternBox.Text = entry.AppName;
        DontRecord.IsChecked = entry.ExcludeLogging;
        NotifyOnly.IsChecked = !entry.ExcludeLogging;
        Loaded += (_, _) => { PatternBox.Focus(); PatternBox.SelectAll(); };
        UpdatePreview();
    }

    public static ExclusionEntry? Show(ExclusionEntry entry, IReadOnlyList<ChangeEvent> history, bool isNew)
    {
        var dialog = new ExclusionDialog(entry, history, isNew)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow
        };
        if (dialog.Owner is { IsVisible: false }) dialog.Owner = null;
        return dialog.ShowDialog() == true ? dialog._result : null;
    }

    private void OnPatternChanged(object sender, TextChangedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var pattern = PatternBox.Text.Trim();
        if (pattern.Length == 0)
        {
            PreviewTitle.Text = "Enter a name to see what it matches";
            PreviewNames.Text = "";
            return;
        }
        var matcher = new ExclusionMatcher([new ExclusionEntry(pattern, true, false)]);
        var hits = _names.Where(n => matcher.Match(n.Name) != null).ToList();
        var total = hits.Sum(h => h.Count);
        PreviewTitle.Text = total == 0 ? "Matches no past events" : $"Matches {total:N0} past event{(total == 1 ? "" : "s")}";
        PreviewNames.Text = string.Join(", ", hits.Take(6).Select(h => Display.CleanName(h.Name))) + (hits.Count > 6 ? $", and {hits.Count - 6} more" : "");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var pattern = PatternBox.Text.Trim();
        if (pattern.Length == 0)
        {
            ErrorText.Text = "Enter an app name or pattern.";
            ErrorText.Visibility = Visibility.Visible;
            PatternBox.Focus();
            return;
        }
        var dontRecord = DontRecord.IsChecked == true;
        _result = new ExclusionEntry(pattern, true, dontRecord);
        DialogResult = true;
    }
}
