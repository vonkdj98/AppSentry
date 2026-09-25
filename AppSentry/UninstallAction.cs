using System.Diagnostics;
using AppSentry.Core;
using AppSentry.Models;

namespace AppSentry;

/// <summary>Confirm-and-run for the "Uninstall…" menu items (history list and Installed Apps).</summary>
internal static class UninstallAction
{
    public static async void Run(IWin32Window owner, InstalledApp app, Action<string> setStatus)
    {
        var plan = Uninstaller.Plan(app);
        if (plan == null)
        {
            MessageBox.Show(owner, $"No uninstall command found for {app.Name}.\n\nYou can uninstall it from Windows Settings > Apps.",
                "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var elevation = plan.Elevate ? "\n\nWindows will ask for administrator approval." : "";
        if (MessageBox.Show(owner, $"Uninstall {app.Name} {app.Version}?\n\n{plan.Display}{elevation}",
                "Confirm Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (plan.IsStore)
        {
            setStatus($"Removing {app.Name}…");
            var error = await Uninstaller.RemoveStorePackageAsync(plan.PackageFullName);
            setStatus(error == null ? $"✓ Removed {app.Name}" : $"Could not remove {app.Name}: {error}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(plan.FileName, plan.Arguments)
            {
                UseShellExecute = true,
                Verb = plan.Elevate ? "runas" : ""
            });
            setStatus($"Started the uninstaller for {app.Name}; the change will appear once it finishes.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            setStatus("Uninstall cancelled.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Failed to start the uninstaller: {ex.Message}", "Uninstall",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
