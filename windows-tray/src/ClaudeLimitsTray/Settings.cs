using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeLimitsTray;

internal enum OverlayMode
{
    WhenClaudeActive,
    Always,
    Off,
}

internal sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public OverlayMode Overlay { get; set; } = OverlayMode.WhenClaudeActive;

    public int OverlayOffsetX { get; set; }

    public int OverlayOffsetY { get; set; } = 150;

    public Dictionary<string, int> Warned { get; set; } = new();

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.Settings), Json) ?? new Settings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Settings unreadable, using defaults: {ex.Message}");
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException ex)
        {
            Log.Write($"Settings not saved: {ex.Message}");
        }
    }
}
