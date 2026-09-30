using System.Text;

namespace Drift.Audio;

static class IcyMetadata
{
    public static TrackInfo Parse(string raw)
    {
        var streamTitle = Field(raw, "StreamTitle")?.Trim();
        var url = Field(raw, "StreamUrl")?.Trim();
        bool advert = raw.Contains("adw_ad='true'", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(streamTitle))
            return new TrackInfo(null, null, url, advert);

        // Most stations send "Artist - Title". Titles can contain " - " too, so split once.
        int dash = streamTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
            return new TrackInfo(streamTitle[..dash].Trim(), streamTitle[(dash + 3)..].Trim(), url, advert);

        return new TrackInfo(null, streamTitle, url, advert);
    }

    static string? Field(string raw, string key)
    {
        int start = raw.IndexOf(key + "='", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += key.Length + 2;
        int end = raw.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0)
            end = raw.LastIndexOf('\'');
        return end > start ? raw[start..end] : "";
    }
}

// Shoutcast/Icecast interleave a metadata block every N audio bytes. This strips them
// out so the mp3 decoder only sees audio, and always fills reads completely because
// Mp3Frame gives up on short reads.
sealed class IcyStream : Stream
{
    static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    readonly Stream inner;
    readonly int interval;
    int untilMeta;
    long position;
    string? lastRaw;

    public IcyStream(Stream inner, int interval)
    {
        this.inner = inner;
        this.interval = interval;
        untilMeta = interval;
    }

    public event Action<TrackInfo>? MetadataChanged;

    public long LastDataTick { get; private set; } = Environment.TickCount64;

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            if (interval > 0 && untilMeta == 0)
            {
                ReadMetadata();
                untilMeta = interval;
            }

            int want = count - total;
            if (interval > 0)
                want = Math.Min(want, untilMeta);

            int read = inner.Read(buffer, offset + total, want);
            if (read <= 0)
                break;

            LastDataTick = Environment.TickCount64;
            position += read;
            total += read;
            if (interval > 0)
                untilMeta -= read;
        }
        return total;
    }

    void ReadMetadata()
    {
        int length = inner.ReadByte();
        if (length <= 0)
            return;

        var block = new byte[length * 16];
        inner.ReadExactly(block);

        string raw;
        try { raw = StrictUtf8.GetString(block); }
        catch (DecoderFallbackException) { raw = Encoding.Latin1.GetString(block); }
        raw = raw.TrimEnd('\0');

        if (raw == lastRaw)
            return;
        lastRaw = raw;
        MetadataChanged?.Invoke(IcyMetadata.Parse(raw));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    // Mp3Frame reads this to note frame offsets, so count audio bytes handed out
    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
