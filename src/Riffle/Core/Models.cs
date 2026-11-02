using System.Text;

namespace Riffle.Core;

public sealed class Track
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Art { get; set; }
    public string? Link { get; set; }
    public string? Wave { get; set; }
    public int Seconds { get; set; }
    public string? Policy { get; set; }

    // Previews and blocked tracks can't be played in full, so they never enter a shuffle.
    public bool IsPlayable => Policy is not ("SNIP" or "BLOCK");

    // Two uploads count as the same song when artist and title match once tidied up.
    public string SameSongKey => Tidy(Artist) + "|" + Tidy(Title.StartsWith(Artist + " - ", StringComparison.OrdinalIgnoreCase)
        ? Title[(Artist.Length + 3)..]
        : Title);

    public string Length => Seconds >= 3600
        ? $"{Seconds / 3600}:{Seconds / 60 % 60:00}:{Seconds % 60:00}"
        : $"{Seconds / 60}:{Seconds % 60:00}";

    static string Tidy(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
                plain.Append(char.ToLowerInvariant(c));
        }
        return plain.ToString();
    }
}

public sealed class Source
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Owner { get; set; } = "";
    public string? Art { get; set; }
    public List<long> Tracks { get; set; } = [];
    public double Weight { get; set; } = 1;
    public DateTime Synced { get; set; }
}

public sealed class SavedMix
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Sources { get; set; } = [];
}

public enum LinkKind { Invalid, Playlist, Likes, Tracks, Profile, Track }

public readonly record struct SoundCloudLink(LinkKind Kind, string Url, string? User);

public static class Links
{
    static readonly HashSet<string> NotUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "discover", "stream", "feed", "you", "search", "upload", "pages", "charts", "tags", "settings", "messages", "notifications",
    };

    // Whatever gets pasted, work out what it points at and tidy it.
    public static SoundCloudLink Parse(string? text)
    {
        var raw = text?.Trim() ?? "";
        if (raw.StartsWith("soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("www.soundcloud.com", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("m.soundcloud.com", StringComparison.OrdinalIgnoreCase))
            raw = "https://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return default;

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("soundcloud.com" or "www.soundcloud.com" or "m.soundcloud.com" or "on.soundcloud.com"))
            return default;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || NotUsers.Contains(parts[0]))
            return default;

        var user = parts[0];
        var baseUrl = $"https://soundcloud.com/{user}";
        var secret = parts.Length >= 4 && parts[1] == "sets" ? "/" + parts[3] : "";

        return parts.Length switch
        {
            >= 3 when parts[1] == "sets" => new(LinkKind.Playlist, $"{baseUrl}/sets/{parts[2]}{secret}", user),
            2 when parts[1] == "likes" => new(LinkKind.Likes, $"{baseUrl}/likes", user),
            2 when parts[1] is "tracks" or "popular-tracks" => new(LinkKind.Tracks, $"{baseUrl}/tracks", user),
            2 when parts[1] is "sets" or "reposts" or "albums" => new(LinkKind.Profile, baseUrl, user),
            1 => new(LinkKind.Profile, baseUrl, user),
            >= 2 => new(LinkKind.Track, $"{baseUrl}/{parts[1]}", user),
            _ => default,
        };
    }
}
