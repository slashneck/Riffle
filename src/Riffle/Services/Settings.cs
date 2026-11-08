using System.Text.Json;

namespace Riffle.Services;

public sealed class Settings
{
    public double Volume { get; set; } = 0.5;
    public bool ShowTile { get; set; } = true;
    public bool ResumeOnStart { get; set; }
    public bool WasPlaying { get; set; }
    public double Crossfade { get; set; } = 6;
    public bool FollowCover { get; set; } = true;
    public string Mode { get; set; } = "True";
    public bool CatchReuploads { get; set; } = true;
    public int LongestMinutes { get; set; }
    public bool BlockAds { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool UltraPerformance { get; set; }
    public Dictionary<string, string> Hotkeys { get; set; } = [];
    public bool OverlayOnChange { get; set; }
    public bool OverlayAlways { get; set; }
    public string OverlayCorner { get; set; } = "TopRight";
    public bool DjOn { get; set; }
    public string DjAmount { get; set; } = "Medium";
    public double DjNewShare { get; set; } = 0.5;
    public bool DjReasons { get; set; } = true;
    public string? AccountName { get; set; }
    public bool EvenLoudness { get; set; }
    public string EqPreset { get; set; } = "Flat";
    public List<Riffle.Core.EqBand> Bands { get; set; } = Riffle.Core.EqBand.Default();
    public double Boost { get; set; } = 6;

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Riffle");
    public static string LocalFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riffle");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            // a broken settings file shouldn't keep the app from starting
        }
        return new Settings();
    }

    public void Save() => Store.Write(FilePath, JsonSerializer.Serialize(this, Options));
}

static class Store
{
    public static void Write(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
        }
    }
}
