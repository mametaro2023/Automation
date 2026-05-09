using System.Media;

namespace AutomationTool;

public sealed class CountdownSoundPlayer
{
    private const int SampleRate = 44100;
    private const short BitsPerSample = 16;
    private const short ChannelCount = 1;
    private const double Volume = 0.28;

    private readonly byte[] _tickSound = CreateSineWaveWav(frequencyHz: 880, durationMs: 80);
    private readonly byte[] _startSound = CreateSineWaveWav(frequencyHz: 1320, durationMs: 120);

    public void PlayTick()
    {
        Play(_tickSound);
    }

    public void PlayStart()
    {
        Play(_startSound);
    }

    private static void Play(byte[] wavBytes)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(wavBytes, writable: false);
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch
            {
                // Countdown sound is non-critical; avoid surfacing audio device errors.
            }
        });
    }

    private static byte[] CreateSineWaveWav(int frequencyHz, int durationMs)
    {
        var sampleCount = Math.Max(1, SampleRate * durationMs / 1000);
        var bytesPerSample = BitsPerSample / 8;
        var dataSize = sampleCount * ChannelCount * bytesPerSample;
        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(ChannelCount);
        writer.Write(SampleRate);
        writer.Write(SampleRate * ChannelCount * bytesPerSample);
        writer.Write((short)(ChannelCount * bytesPerSample));
        writer.Write(BitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataSize);

        var fadeSamples = Math.Min(sampleCount / 2, SampleRate * 8 / 1000);
        for (var i = 0; i < sampleCount; i++)
        {
            var fadeIn = fadeSamples == 0 ? 1.0 : Math.Min(1.0, i / (double)fadeSamples);
            var fadeOut = fadeSamples == 0 ? 1.0 : Math.Min(1.0, (sampleCount - 1 - i) / (double)fadeSamples);
            var envelope = Math.Min(fadeIn, fadeOut);
            var angle = 2.0 * Math.PI * frequencyHz * i / SampleRate;
            var sample = (short)Math.Round(Math.Sin(angle) * short.MaxValue * Volume * envelope);
            writer.Write(sample);
        }

        return stream.ToArray();
    }
}
