using System.Collections.Concurrent;
using Drift.Radio;
using Drift.Services;

namespace Drift.Audio;

// SomaFM asks players to go through their .pls files so listeners get spread across
// their servers instead of everyone hammering one of them.
static class StreamResolver
{
    static readonly ConcurrentDictionary<string, string[]> Playlists = new();

    public static string Resolve(Station station, int attempt, CancellationToken token)
    {
        if (station.Playlist is null)
            return station.Stream ?? throw new InvalidOperationException($"{station.Id} has no stream");

        if (attempt > 0 || !Playlists.TryGetValue(station.Playlist, out var entries))
        {
            entries = Fetch(station.Playlist, token);
            if (entries.Length == 0 && station.Stream is not null)
                entries = [station.Stream];
            Playlists[station.Playlist] = entries;
        }

        if (entries.Length == 0)
            throw new InvalidOperationException($"{station.Id} playlist is empty");

        return entries[Random.Shared.Next(entries.Length)];
    }

    static string[] Fetch(string url, CancellationToken token)
    {
        try
        {
            using var response = Net.Client.Send(new HttpRequestMessage(HttpMethod.Get, url), token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(response.Content.ReadAsStream(token));
            return Parse(reader.ReadToEnd());
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return [];
        }
    }

    static string[] Parse(string pls) =>
        pls.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
            .Select(line => line[(line.IndexOf('=') + 1)..].Trim())
            .Where(entry => entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .ToArray();
}
