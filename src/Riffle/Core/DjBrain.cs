namespace Riffle.Core;

public enum DjLesson { Skip, Keep, Block }

public sealed record Rediscovery(Track Track, double Score, string Reason);

// A song SoundCloud relates to one or more of yours, before Riffle knows anything else about it.
public sealed class DjCandidate(string path, string title, string artist)
{
    public string Path { get; } = path;
    public string Title { get; } = title;
    public string Artist { get; } = artist;
    public HashSet<string> Because { get; } = [];
    public int Votes => Because.Count;
    public string Owner => Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
}

// How the DJ chooses. Nothing in here goes online, it only weighs what it's given.
public static class DjBrain
{
    // A song you know only comes back once you haven't heard it for this long.
    public static readonly TimeSpan Rested = TimeSpan.FromDays(14);
    // and the DJ won't offer the same pick twice inside this.
    public static readonly TimeSpan PickRest = TimeSpan.FromDays(7);

    // Your own songs between two picks.
    public static int Gap(string amount) => amount switch
    {
        "Light" => 5,
        "Heavy" => 1,
        "Raw" => 0,
        _ => 2,
    };

    // Who made a song, the same way for songs in your library and songs found on a page.
    public static string OwnerOf(Track track)
    {
        if (track.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null)
                return first.ToLowerInvariant();
        }
        return track.Artist.ToLowerInvariant();
    }

    public static string PathOf(string? link) =>
        link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.AbsolutePath.TrimEnd('/').ToLowerInvariant() : "";

    // Songs that tell the DJ what you're into: ones you play to the end count most,
    // and the one playing now is always in.
    public static List<Track> Seeds(IReadOnlyCollection<Track> from, Library library, Track? playing, int count, Random random)
    {
        var picked = new List<Track>();
        if (playing is { Link: not null, Secret: null })
            picked.Add(playing);

        var weighted = from
            .Where(t => t.Link is not null && t.Secret is null && t != playing && !library.NeverPlay.Contains(t.Id))
            .Select(t =>
            {
                double weight = 1 + Math.Log2(1 + library.Plays.GetValueOrDefault(t.Id)) * 1.5 - library.Skips.GetValueOrDefault(t.Id);
                weight += library.DjTaste.GetValueOrDefault(OwnerOf(t)) * 0.5;
                return (Track: t, Key: Math.Pow(random.NextDouble(), 1 / Math.Max(0.15, weight)));
            })
            .OrderByDescending(x => x.Key)
            .Take(Math.Max(0, count - picked.Count))
            .Select(x => x.Track);
        picked.AddRange(weighted);
        return picked;
    }

    // Songs you already know that are worth hearing again, best first.
    public static List<Rediscovery> Rediscover(Library library, ISet<long> exclude, DateTime now)
    {
        var known = library.Sources.SelectMany(s => s.Tracks).Concat(library.Plays.Keys).ToHashSet();
        var found = new List<Rediscovery>();
        foreach (var id in known)
        {
            if (exclude.Contains(id) || library.NeverPlay.Contains(id) || !library.Tracks.TryGetValue(id, out var track) || !track.IsPlayable)
                continue;
            if (library.DjPicks.TryGetValue(id, out var pickedAt) && now - pickedAt < PickRest)
                continue;

            int plays = library.Plays.GetValueOrDefault(id);
            int skips = library.Skips.GetValueOrDefault(id);
            bool dated = library.LastPlayed.TryGetValue(id, out var last);
            if (dated && now - last < Rested)
                continue;

            double score;
            string reason;
            if (plays == 0)
            {
                // in your playlists but never heard in Riffle: a smaller bet
                score = 0.5 - skips * 0.5;
                reason = "From your library, not played yet";
            }
            else
            {
                double days = dated ? Math.Min(120, (now - last).TotalDays) : 30;
                score = 1 + Math.Log2(1 + plays) - skips * 0.8 + days / 30;
                reason = dated ? $"Last played {Ago(now - last)}" : plays == 1 ? "Played once, a while back" : $"Played {plays} times, not lately";
            }
            score += library.DjTaste.GetValueOrDefault(OwnerOf(track)) * 0.4;
            if (score > 0)
                found.Add(new Rediscovery(track, score, reason));
        }
        return found.OrderByDescending(f => f.Score).ToList();
    }

    // A new song is better the more of your songs it's related to.
    public static double Score(DjCandidate candidate, Library library, ISet<string> knownOwners)
    {
        double taste = library.DjTaste.GetValueOrDefault(candidate.Owner.ToLowerInvariant());
        if (taste <= -2)
            return double.NegativeInfinity;
        return candidate.Votes + (knownOwners.Contains(candidate.Owner.ToLowerInvariant()) ? 0.3 : 0) + taste * 0.5;
    }

    // Among the best few, not always the very top, so it doesn't feel scripted.
    public static T? Choose<T>(IReadOnlyList<(T Item, double Score)> ranked, Random random, int top = 8) where T : class
    {
        var best = ranked.Where(r => !double.IsNegativeInfinity(r.Score)).Take(top).ToList();
        if (best.Count == 0)
            return null;
        double floor = best.Min(b => b.Score) - 0.5;
        double total = best.Sum(b => b.Score - floor);
        double roll = random.NextDouble() * total;
        foreach (var (item, score) in best)
        {
            roll -= score - floor;
            if (roll <= 0)
                return item;
        }
        return best[^1].Item;
    }

    public static double Learn(double taste, DjLesson lesson) => Math.Clamp(taste + lesson switch
    {
        DjLesson.Keep => 1,
        DjLesson.Skip => -0.5,
        _ => -2,
    }, -3, 3);

    public static string Ago(TimeSpan span) => span.TotalDays switch
    {
        < 21 => "2 weeks ago",
        < 60 => $"{(int)(span.TotalDays / 7)} weeks ago",
        < 365 => $"{(int)(span.TotalDays / 30)} months ago",
        _ => "over a year ago",
    };
}
