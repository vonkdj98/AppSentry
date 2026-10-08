using System.Windows;
using System.Windows.Controls;

namespace AppSentry.Infrastructure;

/// <summary>A search box with a magnifier, a placeholder and a clear button.</summary>
public partial class SearchBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(SearchBox), new PropertyMetadata("Search"));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SearchBox), new PropertyMetadata("Search"));

    public SearchBox() => InitializeComponent();

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    /// <summary>The name a screen reader gives the box.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Puts the cursor in the box (Ctrl+F).</summary>
    public void FocusBox() => Box.Focus();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Text = "";
        Box.Focus();
    }
}
