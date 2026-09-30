using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Drift.Audio;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace Drift.Services;

public sealed record VideoRef(string Id, string Title, string Author, TimeSpan? Duration);

// Only moods built from YouTube playlists or song lists touch YouTube. There's no
// account, and nothing is kept between runs: lookups and audio live in memory.
public sealed partial class YouTube
{
    const long MaxAudioBytes = 160L * 1024 * 1024;

    // versions of a song we never want when looking one up by name
    static readonly string[] Unwanted =
    [
        "slowed", "reverb", "sped up", "8d", "nightcore", "remix", "type beat", "instrumental",
        "cover", "karaoke", "hour", "bass boosted", "mashup", "reaction", "live at", "(live",
        "snippet", "unreleased", "leak", "tiktok", "loop",
    ];

    static readonly string[] Tags =
    [
        "prod", "official", "audio", "video", "lyric", "visualizer", "explicit", "clean", " hd", "hq",
        "mv", "free dl", "remaster", "4k",
    ];

    readonly YoutubeClient client = new();
    readonly ConcurrentDictionary<string, IReadOnlyList<VideoRef>> playlists = new();
    readonly ConcurrentDictionary<string, VideoRef?> songs = new();

    // With a limit it stops after the first page or so, which is enough to start playing
    // while the rest of a long playlist is read in the background.
    public async Task<IReadOnlyList<VideoRef>> PlaylistAsync(string id, CancellationToken token, int limit = int.MaxValue)
    {
        if (playlists.TryGetValue(id, out var cached))
            return cached;

        var list = new List<VideoRef>();
        await foreach (var video in client.Playlists.GetVideosAsync(id, token))
        {
            // live streams have no duration, and anything very short is usually a teaser
            if (video.Duration is { } length && length > TimeSpan.FromMinutes(1) && length < TimeSpan.FromHours(3))
                list.Add(new VideoRef(video.Id.Value, video.Title, video.Author.ChannelTitle, length));
            if (list.Count >= limit)
                return list;
        }
        return playlists[id] = list;
    }

    public async Task<VideoRef?> FindSongAsync(string query, CancellationToken token)
    {
        if (songs.TryGetValue(query, out var known))
            return known;

        var wanted = query.ToLowerInvariant();
        var parts = query.Split(" - ", 2);
        var artist = Simplify(parts[0]);
        var name = Simplify(parts.Length > 1 ? parts[1] : parts[0]);
        VideoRef? best = null;
        int bestScore = int.MinValue, rank = 0;

        await foreach (var video in client.Search.GetVideosAsync(query, token))
        {
            if (++rank > 10)
                break;
            if (video.Duration is not { } length || length < TimeSpan.FromSeconds(75) || length > TimeSpan.FromMinutes(9))
                continue;

            var title = video.Title.ToLowerInvariant();
            if (Unwanted.Any(word => title.Contains(word) && !wanted.Contains(word)))
                continue;

            // search happily returns something unrelated when it can't find the song
            if (name.Length > 0 && !Simplify(video.Title).Contains(name))
                continue;

            var channel = video.Author.ChannelTitle;
            int score = -rank * 2;
            // "Artist - Topic" channels are YouTube's own uploads of the studio versions
            if (channel.EndsWith(" - Topic", StringComparison.Ordinal))
                score += 6;
            if (artist.Length > 0 && Simplify(channel).Contains(artist))
                score += 4;
            if (title.Contains("official audio"))
                score += 2;

            if (score > bestScore)
            {
                bestScore = score;
                best = new VideoRef(video.Id.Value, video.Title, channel, length);
            }
        }

        songs[query] = best;
        return best;
    }

    public async Task<MemoryStream> AudioAsync(string videoId, CancellationToken token)
    {
        var manifest = await client.Videos.Streams.GetManifestAsync(videoId, token);

        // AAC in mp4 is what Windows can decode on its own
        var stream = manifest.GetAudioOnlyStreams()
            .Where(s => s.Container == Container.Mp4)
            .OrderByDescending(s => s.Bitrate)
            .FirstOrDefault() ?? throw new InvalidOperationException("no playable audio");

        if (stream.Size.Bytes > MaxAudioBytes)
            throw new InvalidOperationException("audio is too large");

        var data = new MemoryStream((int)stream.Size.Bytes);
        await client.Videos.Streams.CopyToAsync(stream, data, cancellationToken: token);
        data.Position = 0;
        return data;
    }

    public static TrackInfo Describe(VideoRef video, string? song)
    {
        string? artist = null, title;
        if (song is not null && song.Split(" - ", 2) is [var a, var t])
        {
            // our own song lists are already clean "Artist - Title"
            artist = a;
            title = t;
        }
        else
        {
            var cleaned = Clean(video.Title);
            if (cleaned.Split(" - ", 2) is [var a2, var t2] && a2.Length > 0 && t2.Length > 0)
            {
                artist = a2.Trim();
                title = t2.Trim().Trim('"');
            }
            else
            {
                title = cleaned;
            }
            artist ??= video.Author.Replace(" - Topic", "", StringComparison.Ordinal);
        }

        var art = $"https://i.ytimg.com/vi/{video.Id}/maxresdefault.jpg";
        return new TrackInfo(artist, title, art, VideoId: video.Id);
    }

    static string Clean(string title)
    {
        // [tags] are never part of the name, (parentheses) only when they're tags
        var text = Brackets().Replace(title, "");
        text = Parens().Replace(text, m =>
            Tags.Any(tag => m.Value.Contains(tag, StringComparison.OrdinalIgnoreCase)) ? "" : m.Value);

        var plain = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            // drop emoji and other pictographs that show up in video titles
            if (rune.Value < 0x2190 || (rune.Value >= 0x3000 && rune.Value < 0x1F000))
                plain.Append(rune.ToString());
        }
        return Spaces().Replace(plain.ToString(), " ").Trim(' ', '|', '-', '·', '•');
    }

    static string Simplify(string text) =>
        new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\([^\)]*\)")]
    private static partial Regex Parens();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
