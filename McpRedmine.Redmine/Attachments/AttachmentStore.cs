using System.Collections.Concurrent;

namespace McpRedmine.Redmine.Attachments;

/// <summary>
/// Writes attachments that cannot be returned as text/image to a private folder so the agent can open the original
/// with its own tools, and removes files older than <see cref="Retention"/>. Nothing is stored unless needed.
/// </summary>
public sealed class AttachmentStore(string directory)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public string Root => directory;

    public string Save(string target, int attachmentId, string filename, byte[] bytes)
    {
        var folder = Path.Combine(directory, SafeSegment(target));
        System.IO.Directory.CreateDirectory(folder);
        Purge();

        var path = Path.Combine(folder, $"{attachmentId}-{SafeSegment(filename)}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private void Purge()
    {
        try
        {
            var limit = DateTime.UtcNow - Retention;
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping must never break a tool call.
        }
    }

    /// <summary>One safe path segment: no separators, no leading dots, bounded length.</summary>
    internal static string SafeSegment(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim().TrimStart('.');
        if (cleaned.Length == 0)
            cleaned = "file";
        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }
}

/// <summary>Keeps the last few downloads in memory so paging through one large document does not re-download it each time.</summary>
public sealed class AttachmentBytesCache
{
    private const int MaxEntries = 3;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTime At, byte[] Bytes)> _entries = new();

    public byte[]? Get(string key)
    {
        if (_entries.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.At < Lifetime)
            return entry.Bytes;

        _entries.TryRemove(key, out _);
        return null;
    }

    public void Set(string key, byte[] bytes)
    {
        _entries[key] = (DateTime.UtcNow, bytes);
        foreach (var stale in _entries.OrderByDescending(e => e.Value.At).Skip(MaxEntries).Select(e => e.Key).ToList())
            _entries.TryRemove(stale, out _);
    }
}
