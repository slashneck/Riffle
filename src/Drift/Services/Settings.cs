using System.Text.Json;

namespace Drift.Services;

public sealed class Settings
{
    public double Volume { get; set; } = 0.35;
    public string? MoodId { get; set; }
    public Dictionary<string, string> StationByMood { get; set; } = [];
    public bool ShowInTaskbar { get; set; } = true;
    public bool FindCovers { get; set; } = true;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

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
