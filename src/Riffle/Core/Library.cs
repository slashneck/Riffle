using System.Text.Json;
using Riffle.Services;

namespace Riffle.Core;

// Everything Riffle remembers about your music: song details, never the audio.
public sealed class Library
{
    public Dictionary<long, Track> Tracks { get; set; } = [];
    public List<Source> Sources { get; set; } = [];
    public List<SavedMix> Mixes { get; set; } = [];
    public List<string> Selected { get; set; } = [];
    public HashSet<long> NeverPlay { get; set; } = [];
    public Dictionary<long, int> Plays { get; set; } = [];
    public QueueState? Queue { get; set; }
    public Dictionary<long, DateTime> LastPlayed { get; set; } = [];
    public Dictionary<long, int> Skips { get; set; } = [];
    public Dictionary<long, DateTime> DjPicks { get; set; } = [];
    public Dictionary<string, double> DjTaste { get; set; } = [];

    // Picks you keep land in a playlist of Riffle's own.
    public const string DjFindsUrl = "riffle:dj-finds";
    public int ResumeMs { get; set; }
    public int TotalPlays { get; set; }
    public double TotalMinutes { get; set; }

    static string FilePath => Path.Combine(Settings.Folder, "library.json");

    public static Library Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Library>(File.ReadAllText(FilePath)) ?? new Library();
        }
        catch (Exception)
        {
        }
        return new Library();
    }

    public void Save() => Store.Write(FilePath, JsonSerializer.Serialize(this));

    public readonly record struct Pool(List<Track> Tracks, int Duplicates, int Unplayable, int Excluded);

    // The songs a selection adds up to, each one only once.
    public Pool BuildPool(IEnumerable<string> sourceIds, bool catchReuploads, int longestMinutes)
    {
        var ids = new HashSet<string>(sourceIds);
        var seenIds = new HashSet<long>();
        var seenSongs = new HashSet<string>();
        var result = new List<Track>();
        int duplicates = 0, unplayable = 0, excluded = 0;

        foreach (var source in Sources.Where(s => ids.Contains(s.Id)))
        {
            foreach (var id in source.Tracks)
            {
                if (!seenIds.Add(id))
                {
                    duplicates++;
                    continue;
                }
                if (!Tracks.TryGetValue(id, out var track))
                    continue;
                if (!track.IsPlayable)
                {
                    unplayable++;
                    continue;
                }
                if (NeverPlay.Contains(id) || (longestMinutes > 0 && track.Seconds > longestMinutes * 60))
                {
                    excluded++;
                    continue;
                }
                if (catchReuploads && !seenSongs.Add(track.SameSongKey))
                {
                    duplicates++;
                    continue;
                }
                result.Add(track);
            }
        }
        return new Pool(result, duplicates, unplayable, excluded);
    }

    public double WeightOf(long id)
    {
        double weight = 1;
        foreach (var source in Sources)
        {
            if (source.Weight > weight && Selected.Contains(source.Id) && source.Tracks.Contains(id))
                weight = source.Weight;
        }
        return weight;
    }
}
