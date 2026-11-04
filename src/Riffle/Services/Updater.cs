using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace Riffle.Services;

public sealed record UpdateInfo(string Version, string PageUrl, string DownloadUrl, long Size, string? Sha256);

// Finds new releases on GitHub and installs them. An update only swaps the program files
// named in the release's file list, so the library and settings are never touched.
public static class Updater
{
    public const string Repo = "slashneck/Riffle";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ManifestName = "riffle-files.txt";
    public const string ApplyFlag = "--apply-update";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string InstallFolder => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory.TrimEnd('\\');

    // A build run from a dev folder has no file list and never overwrites itself.
    public static bool CanSelfUpdate => File.Exists(Path.Combine(InstallFolder, ManifestName));

    static string UpdatesFolder => Path.Combine(Settings.LocalFolder, "updates");

    static HttpClient NewClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Riffle/{Current}");
        return client;
    }

    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var client = NewClient();
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        if (!Version.TryParse((root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V').Split('-')[0], out var parsed))
            return null;
        var version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        if (version <= Current)
            return null;

        string zipName = $"Riffle-{version}-win-x64.zip";
        JsonElement? zip = null;
        string? checksumUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.Equals(zipName, StringComparison.OrdinalIgnoreCase))
                zip = asset;
            else if (name.Equals(zipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                checksumUrl = asset.GetProperty("browser_download_url").GetString();
        }
        if (zip is not { } found)
            return null;

        string? sha = found.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.Ordinal)
            ? d[7..]
            : null;
        if (sha is null && checksumUrl is not null)
            sha = (await client.GetStringAsync(checksumUrl)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return new UpdateInfo(version.ToString(), root.GetProperty("html_url").GetString() ?? RepoUrl,
            found.GetProperty("browser_download_url").GetString()!, found.GetProperty("size").GetInt64(), sha);
    }

    // Downloads and checks the release, then unpacks it. Returns the folder holding the new copy.
    public static async Task<string> DownloadAsync(UpdateInfo update, Action<double> progress)
    {
        if (string.IsNullOrWhiteSpace(update.Sha256) || update.Sha256.Length != 64)
            throw new InvalidOperationException("This release has no checksum, so it can't be verified");

        Directory.CreateDirectory(UpdatesFolder);
        string zip = Path.Combine(UpdatesFolder, $"Riffle-{update.Version}.zip");
        using (var client = NewClient())
        using (var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0)
                    progress(Math.Min(1, (double)done / total));
            }
        }

        string actual;
        await using (var stream = File.OpenRead(zip))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidOperationException("The download was damaged, nothing was changed");
        }

        string staged = Path.Combine(UpdatesFolder, $"Riffle-{update.Version}");
        if (Directory.Exists(staged))
            Directory.Delete(staged, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, staged));
        File.Delete(zip);

        if (!File.Exists(Path.Combine(staged, "Riffle.exe")) || !File.Exists(Path.Combine(staged, ManifestName)))
            throw new InvalidOperationException("The download is incomplete, nothing was changed");
        return staged;
    }

    // Starts the downloaded copy, which swaps the files as soon as this process has exited.
    public static void StartInstall(string staged)
    {
        var start = new ProcessStartInfo(Path.Combine(staged, "Riffle.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(ApplyFlag);
        start.ArgumentList.Add(InstallFolder);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(start);
    }

    // Runs from the downloaded copy. Nothing outside the two file lists is touched.
    public static int Apply(string folder, int oldProcess)
    {
        string source = InstallFolder;
        try
        {
            try
            {
                using var old = Process.GetProcessById(oldProcess);
                old.WaitForExit(30_000);
            }
            catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // already gone, or not ours to wait for: the copying below retries while files are held
            }

            if (!File.Exists(Path.Combine(folder, ManifestName)))
                throw new InvalidOperationException("That folder isn't an installed copy of Riffle");
            var newFiles = ReadManifest(source);
            var oldFiles = ReadManifest(folder);

            foreach (string name in oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase))
                DeleteWithRetry(Path.Combine(folder, name));
            foreach (string name in newFiles)
            {
                string to = Path.Combine(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                CopyWithRetry(Path.Combine(source, name), to);
            }
            CopyWithRetry(Path.Combine(source, ManifestName), Path.Combine(folder, ManifestName));
            RemoveEmptyFolders(folder);
            Installation.RefreshEntry(folder);

            Process.Start(new ProcessStartInfo(Path.Combine(folder, "Riffle.exe"), Autostart.Flag) { UseShellExecute = false });
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show($"Riffle couldn't finish the update:\n{error.Message}\n\nYour library and settings are untouched. You can get the new version from {RepoUrl}", "Riffle");
            string exe = Path.Combine(folder, "Riffle.exe");
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            return 1;
        }
    }

    // Removes what's left of a finished update.
    public static void CleanUp()
    {
        if (!Directory.Exists(UpdatesFolder) || InstallFolder.StartsWith(UpdatesFolder, StringComparison.OrdinalIgnoreCase))
            return;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(UpdatesFolder, true);
                    return;
                }
                catch (Exception)
                {
                    // the copy that did the update may still be closing
                    await Task.Delay(3000);
                }
            }
        });
    }

    internal static List<string> ReadManifest(string folder)
    {
        var files = new List<string>();
        foreach (string line in File.ReadAllLines(Path.Combine(folder, ManifestName)))
        {
            string name = line.Trim().Replace('/', '\\');
            if (name.Length == 0)
                continue;
            if (Path.IsPathRooted(name) || name.Split('\\').Contains("..") || name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unexpected entry in the file list: {name}");
            files.Add(name);
        }
        return files;
    }

    static void CopyWithRetry(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 40)
            {
                // antivirus or the old process still holding the file
                Thread.Sleep(250);
            }
        }
    }

    internal static void DeleteWithRetry(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }

    internal static void RemoveEmptyFolders(string root)
    {
        foreach (string folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (Exception)
            {
            }
        }
    }
}
