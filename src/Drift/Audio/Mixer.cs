using NAudio.Wave;

namespace Drift.Audio;

sealed class Mixer : ISampleProvider
{
    public const int Rate = 44100;
    const int RingSize = 4096;

    readonly object gate = new();
    readonly float[] ring = new float[RingSize];
    volatile Deck[] decks = [];
    int ringPos;
    float volume;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

    public volatile float TargetVolume = 0.35f;

    public event Action<Deck>? DeckSilent;

    public void Add(Deck deck)
    {
        lock (gate)
            decks = [.. decks, deck];
    }

    public void Remove(Deck deck)
    {
        lock (gate)
            decks = decks.Where(d => d != deck).ToArray();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);

        foreach (var deck in decks)
        {
            deck.MixInto(buffer, offset, count);
            if (deck.IsSilent && !deck.Retired)
            {
                deck.Retired = true;
                DeckSilent?.Invoke(deck);
            }
        }

        Capture(buffer, offset, count);

        // Smoothed so dragging the volume never clicks. Squared for a curve that
        // feels even across the slider.
        float goal = TargetVolume;
        for (int i = 0; i < count; i += 2)
        {
            volume += (goal - volume) * 0.0012f;
            float gain = volume * volume;
            buffer[offset + i] = Soften(buffer[offset + i] * gain);
            buffer[offset + i + 1] = Soften(buffer[offset + i + 1] * gain);
        }
        return count;
    }

    static float Soften(float x)
    {
        float a = MathF.Abs(x);
        if (a <= 0.85f)
            return x;
        return MathF.CopySign(0.85f + 0.15f * MathF.Tanh((a - 0.85f) / 0.15f), x);
    }

    void Capture(float[] buffer, int offset, int count)
    {
        lock (ring)
        {
            for (int i = 0; i < count; i += 2)
            {
                ring[ringPos] = (buffer[offset + i] + buffer[offset + i + 1]) * 0.5f;
                ringPos = (ringPos + 1) & (RingSize - 1);
            }
        }
    }

    public void CopyRecent(float[] dest)
    {
        lock (ring)
        {
            int start = (ringPos - dest.Length) & (RingSize - 1);
            for (int i = 0; i < dest.Length; i++)
                dest[i] = ring[(start + i) & (RingSize - 1)];
        }
    }

    public void ClearRecent()
    {
        lock (ring)
            Array.Clear(ring);
    }
}
