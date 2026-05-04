using System.IO;
using System.Text.Json;

namespace AutomationTool;

public static class MacroStore
{
    public static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutomationTool");

    public static readonly string DefaultPath = Path.Combine(DefaultDirectory, "macros.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void Save(string path, IEnumerable<Macro> macros)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new MacroFile
        {
            Macros = macros.ToList()
        };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static void SaveDefault(IEnumerable<Macro> macros)
    {
        Save(DefaultPath, macros);
    }

    public static List<Macro> LoadDefault()
    {
        return File.Exists(DefaultPath) ? Load(DefaultPath) : new List<Macro>();
    }

    public static List<Macro> Load(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<MacroFile>(json, JsonOptions);
        return file?.Macros ?? new List<Macro>();
    }
}
