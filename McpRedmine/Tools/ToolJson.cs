using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpRedmine.Tools;

/// <summary>Compact, snake_case JSON without nulls - fewer tokens for the model.</summary>
public static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
