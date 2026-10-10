using NAudio.Dsp;
using NAudio.Wave;

namespace Waveify;

// Three-band equalizer with independent per-channel filter state.
public sealed class EqualizerProvider : ISampleProvider
{
    readonly ISampleProvider source;
    BiQuadFilter[][] filters;
    public WaveFormat WaveFormat => source.WaveFormat;

    public EqualizerProvider(ISampleProvider source)
    {
        this.source = source;
        filters = CreateFilters(0, 0, 0);
    }

    BiQuadFilter[][] CreateFilters(double bass, double mids, double treble)
    {
        return Enumerable.Range(0, WaveFormat.Channels).Select(_ => new[]
        {
            BiQuadFilter.LowShelf(WaveFormat.SampleRate, 160f, 1f, (float)bass),
            BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, 1000f, 1f, (float)mids),
            BiQuadFilter.HighShelf(WaveFormat.SampleRate, 6000f, 1f, (float)treble)
        }).ToArray();
    }

    public void SetGain(double db) => SetBands(db, 0, 0);
    public void SetBands(double bass, double mids, double treble) =>
        filters = CreateFilters(Math.Clamp(bass, -12, 12), Math.Clamp(mids, -12, 12), Math.Clamp(treble, -12, 12));

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var active = filters;
        for (int i = 0; i < read; i++)
        {
            var value = buffer[offset + i];
            foreach (var filter in active[i % active.Length]) value = filter.Transform(value);
            buffer[offset + i] = value;
        }
        return read;
    }
}
