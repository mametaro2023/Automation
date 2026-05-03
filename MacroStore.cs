using System.Text.Json;

namespace AutomationTool;

public static class MacroStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void Save(string path, IEnumerable<Macro> macros)
    {
        var file = new MacroFile
        {
            Macros = macros.ToList()
        };
        var json = JsonSerializer.Serialize(file, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static List<Macro> Load(string path)
    {
        var json = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<MacroFile>(json, JsonOptions);
        return file?.Macros ?? new List<Macro>();
    }
}
