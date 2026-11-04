using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Riffle.Views;

namespace Riffle.Services;

// The per-user install the setup creates: the "Installed apps" entry and uninstalling.
// Uninstalling removes the program and its browser profile. The library and settings stay.
public static class Installation
{
    public const string UninstallFlag = "--uninstall";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Riffle";

    // Only an installed copy has the entry, a copy unpacked from the zip stays out of the list.
    public static void RefreshEntry(string folder)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key is null)
                return;
            long kb = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
            key.SetValue("DisplayVersion", Updater.Current.ToString());
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
        }
        catch (Exception)
        {
        }
    }

    public static int Uninstall()
    {
        string folder = Updater.InstallFolder;
        if (!File.Exists(Path.Combine(folder, Updater.ManifestName)))
        {
            DarkDialog.Show("Nothing to uninstall", "This copy of Riffle wasn't installed by the setup, so you can simply delete its folder.", "OK", null);
            return 1;
        }
        if (!DarkDialog.Show("Uninstall Riffle?", "Riffle is removed from this PC and signed out. Your library and settings stay, in case you come back.", "Uninstall", "Cancel"))
            return 1;

        StopOtherCopies(folder);
        var settings = Settings.Load();
        settings.AccountName = null;
        settings.Save();
        try { Autostart.Enabled = false; } catch (Exception) { }
        foreach (var shortcut in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Riffle.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Riffle.lnk"),
        })
        {
            try { File.Delete(shortcut); } catch (Exception) { }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch (Exception) { }

        string self = Environment.ProcessPath ?? Path.Combine(folder, "Riffle.exe");
        foreach (string name in Updater.ReadManifest(folder))
        {
            string path = Path.Combine(folder, name);
            if (!path.Equals(self, StringComparison.OrdinalIgnoreCase))
                Updater.DeleteWithRetry(path);
        }
        Updater.DeleteWithRetry(Path.Combine(folder, Updater.ManifestName));
        Updater.RemoveEmptyFolders(folder);

        // the browser profile holds the sign-in, the player's processes may need a moment to let go
        for (int attempt = 0; attempt < 8 && Directory.Exists(Settings.LocalFolder); attempt++)
        {
            try { Directory.Delete(Settings.LocalFolder, recursive: true); }
            catch (Exception) { Thread.Sleep(500); }
        }

        DarkDialog.Show("Riffle is uninstalled", $"Your library is still in {Settings.Folder}", "OK", null);

        // A running exe can't delete itself, so a short-lived cmd removes it, the empty folder
        // and the libraries .NET unpacked for it.
        string unpacked = Path.Combine(Path.GetTempPath(), ".net", "Riffle");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{self}\" & rd \"{folder}\" & rd /s /q \"{unpacked}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return 0;
    }

    static void StopOtherCopies(string folder)
    {
        foreach (var process in Process.GetProcessesByName("Riffle"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                        continue;
                    string? path = process.MainModule?.FileName;
                    if (path is null || !path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                        continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
