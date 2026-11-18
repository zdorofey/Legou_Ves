namespace ScaleCore;

public sealed record ScaleConfig(string Address, string Name, string ServiceUuid, string ProtocolName);

public sealed record ScaleAdvertisement(
    string Address,
    string Name,
    short Rssi,
    string ServiceUuid,
    string ProtocolName,
    byte[] Payload,
    DateTimeOffset SeenAt,
    string RawDataSummary = "");

public sealed record WeightReading(
    string Address,
    double WeightKg,
    bool IsStable,
    bool IsRemoved,
    string ProtocolName,
    DateTimeOffset SeenAt,
    byte? ControlByte = null,
    bool HasWeight = true);

public static class ScaleConfigStore
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VesyiMirey", "LegoScale");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "scale.json");

    public static ScaleConfig? Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var json = File.ReadAllText(ConfigPath);
            return System.Text.Json.JsonSerializer.Deserialize<ScaleConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(ScaleConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var json = System.Text.Json.JsonSerializer.Serialize(config,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }
}
