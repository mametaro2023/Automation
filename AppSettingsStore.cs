using System.Text.Json;

namespace AutomationTool;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;
}

public static class AppSettingsStore
{
    public static readonly string DefaultPath = Path.Combine(MacroStore.DefaultDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(DefaultPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(DefaultPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(MacroStore.DefaultDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(DefaultPath, json);
    }
}
