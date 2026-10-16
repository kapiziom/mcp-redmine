using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpRedmine.Config;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Missing file = empty config. Malformed file = <see cref="UserFacingException"/> naming the path.</summary>
    public static RedmineMcpConfig Load(string path)
    {
        if (!File.Exists(path))
            return new RedmineMcpConfig();

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<RedmineMcpConfig>(stream, Options) ?? new RedmineMcpConfig();
        }
        catch (JsonException ex)
        {
            throw new UserFacingException($"Config file '{path}' is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>Atomic write: temp file in the same directory, then replace.</summary>
    public static void Save(string path, RedmineMcpConfig config)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        File.Move(temp, path, overwrite: true);
    }
}
