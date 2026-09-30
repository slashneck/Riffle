namespace Drift.Audio;

public enum NoiseKind { White, Pink, Brown, Rain, Waves }

// Background noise made on the spot, nothing is downloaded. Every kind is tuned to sit
// at roughly the same loudness so switching between them doesn't jump.
sealed class NoiseSynth(NoiseKind kind)
{
    const int Rate = 44100;

    readonly Channel left = new();
    readonly Channel right = new();
    readonly Drop[] drops = new Drop[48];
    uint seed = (uint)Environment.TickCount | 1;

    // rain
    float rainSwell = 1f, rainSwellTarget = 1f;
    int rainSwellLeft;
    float dropBudget;

    // waves
    double wavePhase;
    double waveLength = 9;
    float wave;

    sealed class Channel
    {
        public float B0, B1, B2, B3, B4, B5, B6;
        public float Brown;
        public float Low;
        public float High;
    }

    struct Drop
    {
        public float Level;
        public float Decay;
        public float Pan;
        public float Band;
        public float Low;
    }

    public void Fill(float[] buffer, int count)
    {
        for (int i = 0; i < count; i += 2)
        {
            var (l, r) = kind switch
            {
                NoiseKind.White => (White() * 0.11f, White() * 0.11f),
                NoiseKind.Pink => (Pink(left) * 0.41f, Pink(right) * 0.41f),
                NoiseKind.Brown => (Brown(left) * 1.56f, Brown(right) * 1.56f),
                NoiseKind.Rain => Rain(),
                _ => Waves(),
            };
            buffer[i] = l;
            buffer[i + 1] = r;
        }
    }

    float White()
    {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        return (seed & 0xFFFFFF) / 8388608f - 1f;
    }

    float Chance() => (White() + 1f) * 0.5f;

    // Paul Kellet's pink filter
    float Pink(Channel c)
    {
        float w = White();
        c.B0 = 0.99886f * c.B0 + w * 0.0555179f;
        c.B1 = 0.99332f * c.B1 + w * 0.0750759f;
        c.B2 = 0.96900f * c.B2 + w * 0.1538520f;
        c.B3 = 0.86650f * c.B3 + w * 0.3104856f;
        c.B4 = 0.55000f * c.B4 + w * 0.5329522f;
        c.B5 = -0.7616f * c.B5 - w * 0.0168980f;
        float pink = c.B0 + c.B1 + c.B2 + c.B3 + c.B4 + c.B5 + c.B6 + w * 0.5362f;
        c.B6 = w * 0.115926f;
        return pink * 0.11f;
    }

    float Brown(Channel c)
    {
        c.Brown = (c.Brown + 0.02f * White()) / 1.02f;
        return c.Brown;
    }

    (float, float) Rain()
    {
        // the steady hiss of rain on everything, slowly swelling and easing off
        if (--rainSwellLeft <= 0)
        {
            rainSwellTarget = 0.7f + Chance() * 0.45f;
            rainSwellLeft = Rate * (3 + (int)(Chance() * 5));
        }
        rainSwell += (rainSwellTarget - rainSwell) * 0.00002f;

        float hissL = Pink(left), hissR = Pink(right);
        left.High += (hissL - left.High) * 0.25f;
        right.High += (hissR - right.High) * 0.25f;
        float l = (hissL - left.High * 0.6f) * 0.5f * rainSwell;
        float r = (hissR - right.High * 0.6f) * 0.5f * rainSwell;

        // distant low rumble
        l += Brown(left) * 0.55f;
        r += Brown(right) * 0.55f;

        // individual drops landing close by
        dropBudget += 38f * rainSwell / Rate;
        while (dropBudget >= 1f)
        {
            dropBudget -= 1f;
            Spawn();
        }

        for (int d = 0; d < drops.Length; d++)
        {
            ref var drop = ref drops[d];
            if (drop.Level < 0.0005f)
                continue;
            float n = White() * drop.Level;
            drop.Low += (n - drop.Low) * drop.Band;
            float s = n - drop.Low;
            l += s * (1f - drop.Pan);
            r += s * drop.Pan;
            drop.Level *= drop.Decay;
        }
        return (l * 1.25f, r * 1.25f);
    }

    void Spawn()
    {
        for (int d = 0; d < drops.Length; d++)
        {
            if (drops[d].Level >= 0.0005f)
                continue;
            float closeness = Chance();
            drops[d] = new Drop
            {
                Level = 0.02f + closeness * closeness * 0.16f,
                Decay = 0.9975f - Chance() * 0.006f,
                Pan = 0.15f + Chance() * 0.7f,
                Band = 0.15f + Chance() * 0.5f,
            };
            return;
        }
    }

    (float, float) Waves()
    {
        // each wave rolls in over ~40% of its length and drains away over the rest
        wavePhase += 1.0 / (waveLength * Rate);
        if (wavePhase >= 1)
        {
            wavePhase -= 1;
            waveLength = 7 + Chance() * 5;
        }
        float shape = wavePhase < 0.4
            ? (float)Math.Sin(wavePhase / 0.4 * Math.PI / 2)
            : (float)(0.5 + 0.5 * Math.Cos((wavePhase - 0.4) / 0.6 * Math.PI));
        wave += (shape - wave) * 0.0005f;

        float level = 0.25f + 0.75f * wave;
        float bright = 0.04f + 0.3f * wave * wave;

        float nl = Pink(left) + Brown(left) * 0.8f;
        float nr = Pink(right) + Brown(right) * 0.8f;
        left.Low += (nl - left.Low) * bright;
        right.Low += (nr - right.Low) * bright;
        return (left.Low * level * 0.64f, right.Low * level * 0.64f);
    }
}
