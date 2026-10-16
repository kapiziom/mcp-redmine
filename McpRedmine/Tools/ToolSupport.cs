using McpRedmine.Config;
using McpRedmine.Redmine;

namespace McpRedmine.Tools;

internal static class ToolSupport
{
    public const string TargetDescription =
        "Target name from list_targets. Optional only when exactly one target is configured.";

    /// <summary>Row limit = what the caller asked for, but never more than the target allows.</summary>
    public static (int Limit, int Offset) Page(TargetConfig target, int? limit, int? offset)
    {
        var max = target.EffectiveMaxRows;
        return (limit is null ? max : Math.Clamp(limit.Value, 1, max), Math.Max(0, offset ?? 0));
    }

    public static string? Truncate(string? text, int maxLength)
    {
        if (text is null || text.Length <= maxLength)
            return text;

        return text[..maxLength] + $"... [truncated, {text.Length - maxLength} more characters]";
    }

    public static object? Ref(RedmineIdName? value) =>
        value is null ? null : new { value.Id, value.Name };

    /// <summary>Envelope shared by all list tools: the rows plus how to fetch the next page.</summary>
    public static Dictionary<string, object?> List<T>(
        string key,
        TargetConfig target,
        IReadOnlyList<T> items,
        int offset,
        int? totalCount,
        bool hasMore,
        string? note = null)
    {
        var result = new Dictionary<string, object?>
        {
            ["target"] = target.Name,
            ["offset"] = offset,
            ["returned"] = items.Count,
        };

        if (totalCount is { } total)
            result["total_count"] = total;

        result["has_more"] = hasMore;
        if (hasMore)
            result["next_offset"] = offset + items.Count;
        if (note is not null)
            result["note"] = note;

        result[key] = items;
        return result;
    }
}
