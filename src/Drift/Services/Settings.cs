using System.Text.Json;

namespace Drift.Services;

public sealed class Settings
{
    public double Volume { get; set; } = 0.35;
    public string? MoodId { get; set; }
    public Dictionary<string, string> StationByMood { get; set; } = [];
    public bool ShowInTaskbar { get; set; } = true;
    public bool FindCovers { get; set; } = true;
    public bool ResumeOnStart { get; set; }
    public bool WasPlaying { get; set; }
    public string Crossfade { get; set; } = "normal";
    public bool FollowCover { get; set; } = true;
    public List<CustomPlaylist> Playlists { get; set; } = [];
    public List<string> Blocked { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public double CrossfadeSeconds => Crossfade switch
    {
        "short" => 2,
        "long" => 8,
        _ => 4,
    };

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drift");

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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
        }
    }
}

public sealed record CustomPlaylist(string Name, string Id);
