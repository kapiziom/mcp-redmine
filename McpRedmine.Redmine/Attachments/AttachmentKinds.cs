namespace McpRedmine.Redmine.Attachments;

public enum AttachmentKind
{
    Image,
    Docx,
    Xlsx,
    Pptx,
    Text,
    Pdf,
    Other,
}

public static class AttachmentKinds
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "webp" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "csv", "tsv", "json", "xml", "yml", "yaml", "log", "sql", "html", "htm", "css", "js", "ts",
        "cs", "java", "py", "sh", "bat", "ps1", "ini", "cfg", "conf", "properties", "diff", "patch", "rst", "eml", "svg", "toml",
    };

    public static string Name(AttachmentKind kind) => kind.ToString().ToLowerInvariant();

    public static AttachmentKind Classify(string? filename, string? contentType)
    {
        var extension = Path.GetExtension(filename ?? string.Empty).TrimStart('.').ToLowerInvariant();

        if (ImageExtensions.Contains(extension))
            return AttachmentKind.Image;

        switch (extension)
        {
            case "docx" or "docm":
                return AttachmentKind.Docx;
            case "xlsx" or "xlsm":
                return AttachmentKind.Xlsx;
            case "pptx" or "pptm":
                return AttachmentKind.Pptx;
            case "pdf":
                return AttachmentKind.Pdf;
        }

        if (TextExtensions.Contains(extension))
            return AttachmentKind.Text;

        return contentType switch
        {
            not null when contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) => AttachmentKind.Text,
            not null when contentType.StartsWith("image/png", StringComparison.OrdinalIgnoreCase) => AttachmentKind.Image,
            not null when contentType.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) => AttachmentKind.Image,
            "application/pdf" => AttachmentKind.Pdf,
            "application/json" or "application/xml" => AttachmentKind.Text,
            _ => AttachmentKind.Other,
        };
    }

    /// <summary>MIME type of a supported image judged by its first bytes (never by the file name), or null.</summary>
    public static string? ImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 6 && bytes[..4].SequenceEqual<byte>("GIF8"u8))
            return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual<byte>("RIFF"u8) && bytes[8..12].SequenceEqual<byte>("WEBP"u8))
            return "image/webp";
        return null;
    }

    public static bool IsZip(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && (bytes[2] == 0x03 || bytes[2] == 0x05);
}
