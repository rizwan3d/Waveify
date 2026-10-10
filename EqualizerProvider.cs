using NAudio.Dsp;
using NAudio.Wave;

namespace Waveify;

// Low-shelf style bass tone control applied to each channel independently.
public sealed class EqualizerProvider : ISampleProvider
{
    readonly ISampleProvider source;
    BiQuadFilter[] filters;
    double gain;
    public WaveFormat WaveFormat => source.WaveFormat;

    public EqualizerProvider(ISampleProvider source)
    {
        this.source = source;
        filters = CreateFilters(0);
    }

    BiQuadFilter[] CreateFilters(double db)
    {
        return Enumerable.Range(0, WaveFormat.Channels)
            .Select(_ => BiQuadFilter.LowShelf(WaveFormat.SampleRate, 160f, 1f, (float)db)).ToArray();
    }

    public void SetGain(double db)
    {
        gain = Math.Clamp(db, -12, 12);
        filters = CreateFilters(gain);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        var active = filters;
        for (int i = 0; i < read; i++)
            buffer[offset + i] = active[i % active.Length].Transform(buffer[offset + i]);
        return read;
    }
}
