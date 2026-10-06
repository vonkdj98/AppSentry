using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace AppSentry.Services;

/// <summary>Message boxes and shell helpers, owned by the main window when it's visible.</summary>
public static class Dialogs
{
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    public static bool Confirm(string title, string message, bool destructive = false) =>
        Show(message, title, MessageBoxButton.YesNo, destructive ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void ShowInfo(string title, string message) => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void ShowError(string title, string message) => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Owner;
        return owner is { IsVisible: true }
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }

    public static string? SaveFile(string title, string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Open folder", $"Couldn't open {path}: {ex.Message}");
        }
    }

    /// <summary>Puts text on the clipboard; false when another program holds it (not worth a dialog, but don't say "Copied").</summary>
    public static bool CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>"Start with Windows": the per-user Run key, pointing at this exe with --minimized.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AppSentry";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) != null;
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("The Run key isn't writable.");
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
