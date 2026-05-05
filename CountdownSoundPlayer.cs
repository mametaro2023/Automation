using System.Media;

namespace AutomationTool;

public sealed class CountdownSoundPlayer
{
    private readonly Random _random = new();

    public void PlayTick()
    {
        PlayRandom("count.wav", "keyboard_on-*.wav");
    }

    public void PlayStart()
    {
        PlayRandom("click_on-*.wav");
    }

    private void PlayRandom(params string[] patterns)
    {
        var files = FindSoundFiles(patterns);
        if (files.Count == 0)
        {
            SystemSounds.Beep.Play();
            return;
        }

        try
        {
            var path = files[_random.Next(files.Count)];
            _ = Task.Run(() =>
            {
                using var player = new SoundPlayer(path);
                player.PlaySync();
            });
        }
        catch
        {
            SystemSounds.Beep.Play();
        }
    }

    private static List<string> FindSoundFiles(IReadOnlyList<string> patterns)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "sounds"),
            Path.Combine(Environment.CurrentDirectory, "sounds")
        };

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(directory))
            {
                foreach (var pattern in patterns)
                {
                    var files = Directory.GetFiles(directory, pattern)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (files.Count > 0)
                    {
                        return files;
                    }
                }
            }
        }

        return new List<string>();
    }
}
