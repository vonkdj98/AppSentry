using AppSentry.Core.Sources;
using AppSentry.Models;

namespace AppSentry.Demo;

/// <summary>
/// Entirely made-up inventory, services and tasks for public screenshots (--synthetic): common
/// public apps with plausible versions, a fictional "Contoso" org and user. Icon paths point at
/// the standard Program Files locations, so real icons appear where those apps happen to be
/// installed on the machine taking the screenshot — no names, paths or data from that machine.
/// </summary>
public static class SyntheticData
{
    private sealed record App(string Name, string Publisher, string Version, int SizeMb, string Folder, string? Icon, int DaysAgo, string Type = "MSI");

    private static readonly App[] Apps =
    [
        new("7-Zip 24.08 (x64)", "Igor Pavlov", "24.08", 6, @"C:\Program Files\7-Zip", @"C:\Program Files\7-Zip\7zFM.exe", 40, "NSIS"),
        new("Adobe Acrobat Reader", "Adobe", "25.001.20531", 720, @"C:\Program Files\Adobe\Acrobat DC", @"C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe", 12),
        new("Docker Desktop", "Docker Inc.", "4.34.2", 2900, @"C:\Program Files\Docker\Docker", @"C:\Program Files\Docker\Docker\Docker Desktop.exe", 60, "Unknown"),
        new("Git", "The Git Development Community", "2.47.1.2", 340, @"C:\Program Files\Git", @"C:\Program Files\Git\git-bash.exe", 33, "InnoSetup"),
        new("Google Chrome", "Google LLC", "140.0.7339.186", 610, @"C:\Program Files\Google\Chrome\Application", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 3),
        new("Greenshot 1.2.10.6", "Greenshot", "1.2.10.6", 9, @"C:\Program Files\Greenshot", @"C:\Program Files\Greenshot\Greenshot.exe", 210, "InnoSetup"),
        new("KeePassXC", "KeePassXC Team", "2.7.9", 110, @"C:\Program Files\KeePassXC", @"C:\Program Files\KeePassXC\KeePassXC.exe", 95),
        new("Microsoft 365 Apps for enterprise - en-us", "Microsoft Corporation", "16.0.18925.20158", 3900, @"C:\Program Files\Microsoft Office", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", 20, "Unknown"),
        new("Microsoft Edge", "Microsoft Corporation", "140.0.3485.81", 2800, @"C:\Program Files (x86)\Microsoft\Edge\Application", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", 5, "Unknown"),
        new("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", "Microsoft Corporation", "14.44.35211.0", 21, "", null, 70, "WiX Bundle"),
        new("Microsoft Visual Studio Code", "Microsoft Corporation", "1.104.1", 460, @"C:\Program Files\Microsoft VS Code", @"C:\Program Files\Microsoft VS Code\Code.exe", 9, "InnoSetup"),
        new("Mozilla Firefox (x64 en-US)", "Mozilla", "143.0.1", 250, @"C:\Program Files\Mozilla Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", 6, "Unknown"),
        new("Node.js", "Node.js Foundation", "22.9.0", 95, @"C:\Program Files\nodejs", @"C:\Program Files\nodejs\node.exe", 44),
        new("Notepad++ (64-bit x64)", "Notepad++ Team", "8.8.5", 16, @"C:\Program Files\Notepad++", @"C:\Program Files\Notepad++\notepad++.exe", 18, "NSIS"),
        new("Paint.NET", "dotPDN LLC", "5.1.9", 320, @"C:\Program Files\paint.net", @"C:\Program Files\paint.net\paintdotnet.exe", 120),
        new("PowerToys (Preview) x64", "Microsoft Corporation", "0.94.0", 1300, @"C:\Program Files\PowerToys", @"C:\Program Files\PowerToys\PowerToys.exe", 25, "WiX Bundle"),
        new("PuTTY release 0.83 (64-bit)", "Simon Tatham", "0.83.0.0", 5, @"C:\Program Files\PuTTY", @"C:\Program Files\PuTTY\putty.exe", 150),
        new("Python 3.12.6 (64-bit)", "Python Software Foundation", "3.12.6150.0", 105, @"C:\Program Files\Python312", @"C:\Program Files\Python312\python.exe", 80, "WiX Bundle"),
        new("VLC media player", "VideoLAN", "3.0.21", 170, @"C:\Program Files\VideoLAN\VLC", @"C:\Program Files\VideoLAN\VLC\vlc.exe", 300, "NSIS"),
        new("WinSCP 6.5.3", "Martin Prikryl", "6.5.3", 55, @"C:\Program Files (x86)\WinSCP", @"C:\Program Files (x86)\WinSCP\WinSCP.exe", 66, "InnoSetup"),
        new("Wireshark 4.4.9 x64", "The Wireshark developer community", "4.4.9", 240, @"C:\Program Files\Wireshark", @"C:\Program Files\Wireshark\Wireshark.exe", 38, "NSIS"),
        new("Zoom Workplace (64-bit)", "Zoom Communications, Inc.", "6.5.11", 340, @"C:\Program Files\Zoom", @"C:\Program Files\Zoom\bin\Zoom.exe", 1),
        new("Contoso Remote Agent", "Contoso Ltd.", "3.2.0", 48, @"C:\Program Files\Contoso\Agent", null, 400),
        new("Contoso VPN Client", "Contoso Ltd.", "4.1.8", 90, @"C:\Program Files\Contoso\VPN", null, 400)
    ];

    public static List<InstalledApp> Inventory()
    {
        var today = DateTime.Today;
        return Apps.Select(a =>
        {
            var key = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{new string(a.Name.Where(char.IsLetterOrDigit).ToArray())}";
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DisplayName"] = a.Name,
                ["DisplayVersion"] = a.Version,
                ["Publisher"] = a.Publisher,
                ["InstallLocation"] = a.Folder,
                ["UninstallString"] = a.Folder.Length > 0 ? $@"""{a.Folder}\uninstall.exe""" : "",
                ["EstimatedSize"] = (a.SizeMb * 1024).ToString()
            };
            if (a.Icon != null) raw["DisplayIcon"] = a.Icon;
            return new InstalledApp
            {
                KeyPath = key,
                Scope = Scopes.Machine64,
                Name = a.Name,
                Version = a.Version,
                Publisher = a.Publisher,
                InstallDate = today.AddDays(-a.DaysAgo).ToString("yyyyMMdd"),
                InstallLocation = a.Folder,
                InstallType = a.Type,
                InstalledFor = "All users",
                UninstallString = raw["UninstallString"],
                EstimatedSizeKb = a.SizeMb * 1024L,
                RawValues = raw
            };
        }).ToList();
    }

    public static PersistenceInventory Persistence()
    {
        ServiceRecord Svc(string display, string name, string path, int start = 2, string account = "LocalSystem", bool driver = false, bool microsoft = false) =>
            new() { DisplayName = display, Name = name, ImagePath = path, Start = start, Account = driver ? "" : account, IsDriver = driver, IsMicrosoft = microsoft };
        TaskRecord Task(string path, string actions, string runAs, bool highest = false, string author = "Contoso Ltd.") =>
            new() { Path = path, Actions = actions, RunAs = runAs, Highest = highest, Author = author, Enabled = true };

        return new PersistenceInventory
        {
            Services =
            [
                Svc("Adobe Acrobat Update Service", "AdobeARMservice", @"""C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\armsvc.exe"""),
                Svc("AppSentry Monitor", "AppSentry", @"""C:\Program Files\AppSentry\AppSentry.exe"" --service"),
                Svc("Contoso Remote Agent", "ContosoAgent", @"""C:\Program Files\Contoso\Agent\agent.exe"" -service"),
                Svc("Docker Desktop Service", "com.docker.service", @"""C:\Program Files\Docker\Docker\com.docker.service"""),
                Svc("Google Chrome Elevation Service", "GoogleChromeElevationService", @"""C:\Program Files\Google\Chrome\Application\140.0.7339.186\elevation_service.exe""", start: 3),
                Svc("Mozilla Maintenance Service", "MozillaMaintenance", @"""C:\Program Files (x86)\Mozilla Maintenance Service\maintenanceservice.exe""", start: 3),
                Svc("OpenSSH Authentication Agent", "ssh-agent", @"C:\Windows\System32\OpenSSH\ssh-agent.exe", start: 4, microsoft: true),
                Svc("Zoom Sharing Service", "ZoomCptService", @"""C:\Program Files\Zoom\bin\CptService.exe""", start: 3),
                Svc("Npcap Packet Driver (NPCAP)", "npcap", @"\SystemRoot\system32\DRIVERS\npcap.sys", start: 1, driver: true),
                Svc("Contoso Filter Driver", "cfltr", @"\SystemRoot\System32\drivers\cfltr.sys", start: 0, driver: true),
                Svc("VirtualBox Support Driver", "VBoxSup", @"\SystemRoot\system32\DRIVERS\VBoxSup.sys", start: 1, driver: true)
            ],
            Tasks =
            [
                Task(@"\Adobe Acrobat Update Task", @"C:\Program Files (x86)\Common Files\Adobe\ARM\1.0\AdobeARM.exe", "INTERACTIVE", author: "Adobe Systems Incorporated"),
                Task(@"\Contoso\UpdaterTaskMachineCore", @"C:\Program Files (x86)\Contoso\Update\ContosoUpdate.exe /c", "SYSTEM", highest: true),
                Task(@"\GoogleSystem\GoogleUpdater\GoogleUpdaterTaskSystem140.0", @"""C:\Program Files (x86)\Google\GoogleUpdater\updater.exe"" --wake --system", "SYSTEM", highest: true, author: "Google LLC"),
                Task(@"\Mozilla\Firefox Background Update", @"C:\Program Files\Mozilla Firefox\firefox.exe --backgroundtask backgroundupdate", @"CONTOSO\alex", author: "Mozilla"),
                Task(@"\PowerToys\Autorun for alex", @"C:\Program Files\PowerToys\PowerToys.exe", @"CONTOSO\alex", highest: true, author: @"CONTOSO\alex")
            ]
        };
    }
}
