using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Drift.Radio;

public sealed class Station
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? FullName { get; init; }
    public string Blurb { get; init; } = "";
    public string Source { get; init; } = "";
    // "radio", "youtube" or "noise"
    public string Kind { get; init; } = "radio";
    public string? Stream { get; init; }
    public string? Playlist { get; init; }
    public List<string> Playlists { get; init; } = [];
    public List<string> Songs { get; init; } = [];
    public string? Noise { get; init; }

    // "stream" = cover url arrives with the track (radio metadata or a video thumbnail),
    // "lookup" = search by artist and title first, "nts:<alias>" = NTS mixtape artwork,
    // "none" = always the generated artwork
    public string Art { get; init; } = "lookup";
    public string? Logo { get; init; }
    public string Tint { get; init; } = "#8FA3C0";
    public string? Support { get; init; }

    [JsonIgnore] public Mood Mood { get; internal set; } = null!;
    [JsonIgnore] public string Title => FullName ?? Name;
    [JsonIgnore] public bool IsCustom { get; init; }
    [JsonIgnore] public Color TintColor => (Color)ColorConverter.ConvertFromString(Tint);

    public override string ToString() => Title;
}

public sealed class Mood
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<Station> Stations { get; init; } = [];

    public override string ToString() => Name;
}

public static class StationList
{
    sealed class File
    {
        public List<Mood> Moods { get; init; } = [];
    }

    public static IReadOnlyList<Mood> Load()
    {
        using var stream = typeof(StationList).Assembly.GetManifestResourceStream("Drift.stations.json")
            ?? throw new InvalidOperationException("stations.json is missing from the build");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var file = JsonSerializer.Deserialize<File>(stream, options) ?? new File();

        foreach (var mood in file.Moods)
            foreach (var station in mood.Stations)
                station.Mood = mood;

        return file.Moods;
    }
}
