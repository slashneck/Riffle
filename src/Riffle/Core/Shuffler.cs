using System.Security.Cryptography;

namespace Riffle.Core;

public enum ShuffleMode { True, SpreadArtists, FreshFirst }

// Every ordering of the songs is equally likely. Random numbers come from the system's
// cryptographic generator, and GetInt32 has no modulo bias, so nothing favors a position.
public static class Shuffler
{
    public static List<T> Shuffle<T>(IEnumerable<T> items)
    {
        var list = items.ToList();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // Still random, but heavier songs tend to come earlier (Efraimidis and Spirakis).
    // With equal weights this is the same as a plain shuffle.
    public static List<T> ShuffleWeighted<T>(IEnumerable<T> items, Func<T, double> weight)
    {
        var keyed = new List<(double Key, T Item)>();
        foreach (var item in items)
        {
            double u = (RandomNumberGenerator.GetInt32(1, int.MaxValue)) / (double)int.MaxValue;
            keyed.Add((-Math.Log(u) / Math.Max(0.01, weight(item)), item));
        }
        keyed.Sort((a, b) => a.Key.CompareTo(b.Key));
        return keyed.Select(k => k.Item).ToList();
    }

    public static List<Track> Order(IReadOnlyCollection<Track> pool, ShuffleMode mode,
        Func<Track, double> weight, Func<Track, int> timesPlayed)
    {
        bool weighted = pool.Any(t => Math.Abs(weight(t) - 1) > 0.001);
        List<Track> Mix(IEnumerable<Track> tracks) => weighted ? ShuffleWeighted(tracks, weight) : Shuffle(tracks);

        return mode switch
        {
            ShuffleMode.SpreadArtists => SpreadArtists(Mix(pool)),
            ShuffleMode.FreshFirst => pool.GroupBy(t => Math.Min(timesPlayed(t), 3)).OrderBy(g => g.Key)
                .SelectMany(g => Mix(g)).ToList(),
            _ => Mix(pool),
        };
    }

    // Pulls the nearest different artist forward when one would play twice in a row.
    public static List<Track> SpreadArtists(List<Track> order)
    {
        for (int i = 1; i < order.Count; i++)
        {
            if (!SameArtist(order[i], order[i - 1]))
                continue;
            for (int k = i + 1; k < order.Count; k++)
            {
                if (SameArtist(order[k], order[i - 1]))
                    continue;
                var moved = order[k];
                order.RemoveAt(k);
                order.Insert(i, moved);
                break;
            }
        }
        return order;
    }

    static bool SameArtist(Track a, Track b) =>
        string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
}
