using Drift.Radio;

namespace Drift.Audio;

sealed class NoiseSource : Source
{
    readonly NoiseSynth synth;

    public NoiseSource(Station station, SynchronizationContext ui) : base(station, ui)
    {
        var kind = Enum.TryParse<NoiseKind>(station.Noise, true, out var parsed) ? parsed : NoiseKind.Pink;
        synth = new NoiseSynth(kind);
    }

    public override void Start() { }

    protected override bool Render(float[] buffer, int count)
    {
        synth.Fill(buffer, count);
        return true;
    }
}
