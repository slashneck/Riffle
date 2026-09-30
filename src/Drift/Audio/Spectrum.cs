using NAudio.Dsp;

namespace Drift.Audio;

// Log spaced band levels (0..1) of whatever is currently coming out of the mixer.
public sealed class Spectrum
{
    public const int Bands = 32;
    const int Size = 1024;
    const int Order = 10;

    readonly Mixer mixer;
    readonly float[] samples = new float[Size];
    readonly float[] window = new float[Size];
    readonly Complex[] bins = new Complex[Size];
    readonly int[] edges = new int[Bands + 1];
    readonly float[] levels = new float[Bands];
    long lastTick;

    internal Spectrum(Mixer mixer)
    {
        this.mixer = mixer;

        for (int i = 0; i < Size; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1)));

        const double low = 45, high = 14000;
        for (int b = 0; b <= Bands; b++)
        {
            double freq = low * Math.Pow(high / low, (double)b / Bands);
            int bin = (int)Math.Round(freq * Size / Mixer.Rate);
            edges[b] = b == 0 ? Math.Max(1, bin) : Math.Max(edges[b - 1] + 1, bin);
        }
    }

    public ReadOnlySpan<float> Read()
    {
        long now = Environment.TickCount64;
        if (now - lastTick < 12)
            return levels;
        lastTick = now;

        mixer.CopyRecent(samples);
        for (int i = 0; i < Size; i++)
        {
            bins[i].X = samples[i] * window[i];
            bins[i].Y = 0;
        }
        FastFourierTransform.FFT(true, Order, bins);

        for (int b = 0; b < Bands; b++)
        {
            float peak = 0;
            for (int k = edges[b]; k < edges[b + 1] && k < Size / 2; k++)
            {
                float mag = MathF.Sqrt(bins[k].X * bins[k].X + bins[k].Y * bins[k].Y);
                if (mag > peak)
                    peak = mag;
            }

            float db = 20f * MathF.Log10(peak * 4f + 1e-7f);
            float tilt = 12f * b / (Bands - 1);
            levels[b] = Math.Clamp((db + tilt + 60f) / 48f, 0f, 1f);
        }
        return levels;
    }
}
