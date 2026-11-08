using System.Text.Json;
using Riffle.Core;
using Riffle.Services;

namespace Riffle.Playback;

public sealed record DjPick(Track Track, string Reason, bool New);

// Finds the next pick: half the time something you know and haven't heard in a while,
// half the time something new from SoundCloud's related songs. Shifts with the slider.
sealed class Dj(Library library, PlayerHost host, Settings settings, Func<JsonElement, Track> read)
{
    const int SeedsPerScan = 2;

    readonly Dictionary<string, DjCandidate> candidates = [];
    readonly HashSet<string> scanned = [];
    readonly HashSet<string> tried = [];
    readonly Dictionary<string, TaskCompletionSource<JsonElement?>> describing = [];
    readonly Random random = new();
    string? lastOwner;
    int requests;

    public async Task<DjPick?> PickAsync(IReadOnlyCollection<Track> seedsFrom, Track? playing, ISet<long> exclude)
    {
        bool wantNew = random.NextDouble() < settings.DjNewShare;
        var pick = wantNew
            ? await DiscoverAsync(seedsFrom, playing, exclude) ?? Rediscover(exclude)
            : Rediscover(exclude) ?? await DiscoverAsync(seedsFrom, playing, exclude);
        if (pick is not null)
        {
            library.DjPicks[pick.Track.Id] = DateTime.UtcNow;
            lastOwner = DjBrain.OwnerOf(pick.Track);
        }
        return pick;
    }

    public void Learn(Track track, DjLesson lesson)
    {
        var owner = DjBrain.OwnerOf(track);
        library.DjTaste[owner] = DjBrain.Learn(library.DjTaste.GetValueOrDefault(owner), lesson);
    }

    DjPick? Rediscover(ISet<long> exclude)
    {
        var ranked = DjBrain.Rediscover(library, exclude, DateTime.UtcNow)
            .Where(r => DjBrain.OwnerOf(r.Track) != lastOwner)
            .Select(r => (Item: r, r.Score))
            .ToList();
        return DjBrain.Choose(ranked, random) is { } found ? new DjPick(found.Track, found.Reason, false) : null;
    }

    async Task<DjPick?> DiscoverAsync(IReadOnlyCollection<Track> seedsFrom, Track? playing, ISet<long> exclude)
    {
        var knownPaths = library.Tracks.Values.Select(t => DjBrain.PathOf(t.Link)).Where(p => p.Length > 0).ToHashSet();
        var knownIds = library.Sources.SelectMany(s => s.Tracks).Concat(library.Plays.Keys).ToHashSet();
        var knownOwners = library.Tracks.Values.Select(DjBrain.OwnerOf).ToHashSet();

        for (int round = 0; round < 3; round++)
        {
            if (Fresh(knownPaths).Count() < 6)
                await ScanAsync(seedsFrom, playing, knownPaths);

            var ranked = Fresh(knownPaths)
                .Where(c => c.Owner.ToLowerInvariant() != lastOwner)
                .Select(c => (Item: c, Score: DjBrain.Score(c, library, knownOwners)))
                .OrderByDescending(c => c.Score)
                .ToList();
            if (ranked.Count == 0)
                continue;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (DjBrain.Choose(ranked, random) is not { } candidate)
                    break;
                tried.Add(candidate.Path);
                ranked.RemoveAll(r => r.Item == candidate);
                if (await DescribeAsync("https://soundcloud.com" + candidate.Path) is not { } track)
                    continue;
                if (!Fits(track, exclude, knownIds))
                    continue;
                library.Tracks[track.Id] = track;
                var because = candidate.Because.FirstOrDefault();
                return new DjPick(track, because is null ? "New to you" : $"New  ·  because you play {because}", true);
            }
        }
        return null;
    }

    IEnumerable<DjCandidate> Fresh(ISet<string> knownPaths) =>
        candidates.Values.Where(c => !tried.Contains(c.Path) && !knownPaths.Contains(c.Path.ToLowerInvariant()));

    bool Fits(Track track, ISet<long> exclude, ISet<long> knownIds)
    {
        int longest = settings.LongestMinutes > 0 ? settings.LongestMinutes * 60 : 15 * 60;
        return track.IsPlayable && track.Seconds >= 60 && track.Seconds <= longest
            && !exclude.Contains(track.Id) && !knownIds.Contains(track.Id) && !library.NeverPlay.Contains(track.Id)
            && !(library.DjPicks.TryGetValue(track.Id, out var at) && DateTime.UtcNow - at < DjBrain.PickRest);
    }

    async Task ScanAsync(IReadOnlyCollection<Track> seedsFrom, Track? playing, ISet<string> knownPaths)
    {
        var seeds = DjBrain.Seeds(seedsFrom, library, playing, 8, random).Where(s => !scanned.Contains(s.Link!)).Take(SeedsPerScan).ToList();
        foreach (var seed in seeds)
        {
            scanned.Add(seed.Link!);
            List<(string Path, string Title, string Artist)> related;
            try
            {
                related = await host.ScanRelatedAsync(seed.Link!.TrimEnd('/') + "/recommended");
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var (path, title, artist) in related)
            {
                if (knownPaths.Contains(path.ToLowerInvariant()))
                    continue;
                if (!candidates.TryGetValue(path, out var candidate))
                    candidates[path] = candidate = new DjCandidate(path, title, artist);
                candidate.Because.Add(seed.Artist);
            }
        }
    }

    async Task<Track?> DescribeAsync(string url)
    {
        var id = "d" + ++requests;
        var waiting = new TaskCompletionSource<JsonElement?>();
        describing[id] = waiting;
        host.Send(new { cmd = "describe", id, url });
        var done = await Task.WhenAny(waiting.Task, Task.Delay(25000));
        describing.Remove(id);
        if (done != waiting.Task || await waiting.Task is not { } sound)
            return null;
        if (sound.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.True)
            return null;
        try { return read(sound); }
        catch (Exception) { return null; }
    }

    public void OnDescribed(string id, JsonElement message)
    {
        if (!describing.TryGetValue(id, out var waiting))
            return;
        waiting.TrySetResult(message.TryGetProperty("sound", out var sound) && sound.ValueKind == JsonValueKind.Object ? sound.Clone() : null);
    }
}
