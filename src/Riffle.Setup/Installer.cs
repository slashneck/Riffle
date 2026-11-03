using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RiffleSetup;

/// <summary>
/// Copies Riffle into a per-user folder. Only files named in Riffle's file list are ever replaced or removed, so
/// nothing else in the folder, and nothing in %AppData%, is touched.
/// </summary>
internal static class Installer
{
    public const string ManifestName = "riffle-files.txt";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Riffle";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WebView2ClientKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Riffle");

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("payload.zip");

    public static bool IsInstalled(string folder) => File.Exists(Path.Combine(folder, ManifestName));

    public static bool IsRunning(string folder)
    {
        var copies = RunningCopies(folder);
        copies.ForEach(p => p.Dispose());
        return copies.Count > 0;
    }

    public static void Install(SetupOptions options, Action<double, string> report)
    {
        if (!HasPayload) throw new InvalidOperationException("This setup doesn't contain Riffle. Download the full setup from GitHub.");
        string folder = options.Folder;

        report?.Invoke(0, "Closing Riffle");
        foreach (var proc in RunningCopies(folder))
        {
            using (proc)
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(5000);
                }
                catch { }
            }
        }

        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            var manifest = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("The setup is damaged: its file list is missing.");
            List<string> newFiles;
            using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8)) newFiles = ParseManifest(reader.ReadToEnd());
            var oldFiles = IsInstalled(folder) ? ParseManifest(File.ReadAllText(Path.Combine(folder, ManifestName))) : new List<string>();

            Directory.CreateDirectory(folder);
            report?.Invoke(0.02, "Removing old files");
            foreach (string rel in oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase))
                Retry(() => { string path = Path.Combine(folder, rel); if (File.Exists(path)) File.Delete(path); });

            var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
            long total = Math.Max(1, entries.Sum(e => e.Length)), done = 0;
            foreach (var entry in entries)
            {
                string target = Path.Combine(folder, CheckRelative(entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                Retry(() =>
                {
                    using (var input = entry.Open())
                    using (var output = File.Create(target))
                        input.CopyTo(output);
                });
                done += entry.Length;
                report?.Invoke(0.05 + 0.85 * done / total, "Copying files");
            }
        }
        RemoveEmptyFolders(folder);

        string exe = Path.Combine(folder, "Riffle.exe");
        report?.Invoke(0.94, "Adding shortcuts");
        if (options.StartMenuShortcut) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Riffle.lnk"), exe);
        if (options.DesktopShortcut) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Riffle.lnk"), exe);
        if (options.Register)
        {
            Register(folder, exe);
            UpdateAutostart(exe);
        }
        report?.Invoke(1, "Done");
    }

    public static void Launch(string folder)
    {
        string exe = Path.Combine(folder, "Riffle.exe");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = folder });
    }

    /// <summary>Riffle plays through the WebView2 runtime. Windows 11 always has it, most Windows 10 PCs too.</summary>
    public static bool HasWebView2()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var key = root.OpenSubKey(WebView2ClientKey))
                {
                    if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static List<Process> RunningCopies(string folder)
    {
        string prefix = folder.TrimEnd('\\') + "\\";
        var result = new List<Process>();
        foreach (var proc in Process.GetProcessesByName("Riffle"))
        {
            string path = null;
            try { path = proc.MainModule?.FileName; } catch { }
            if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(proc);
            else proc.Dispose();
        }
        return result;
    }

    private static List<string> ParseManifest(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).Select(CheckRelative).ToList();

    private static string CheckRelative(string path)
    {
        string rel = path.Replace('/', '\\');
        if (Path.IsPathRooted(rel) || rel.Split('\\').Contains("..")) throw new InvalidDataException($"Unexpected file in the setup: {path}");
        return rel;
    }

    private static void Retry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 40)
            {
                Thread.Sleep(250); // a closing Riffle or an antivirus scan can hold a file for a moment
            }
        }
    }

    private static void RemoveEmptyFolders(string root)
    {
        foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
    }

    private static void Register(string folder, string exe)
    {
        long kb = new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "Riffle");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "slashneck");
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", folder);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("URLInfoAbout", "https://github.com/slashneck/Riffle");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
        }
    }

    /// <summary>If Riffle already starts with Windows from another folder, point that entry at this install.</summary>
    private static void UpdateAutostart(string exe)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            if (key?.GetValue("Riffle") != null) key.SetValue("Riffle", $"\"{exe}\" --background");
        }
    }

    private static void CreateShortcut(string path, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target));
        link.SetIconLocation(target, 0);
        link.SetDescription("Riffle");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        ((IPersistFile)link).Save(path, false);
        Marshal.ReleaseComObject(link);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
