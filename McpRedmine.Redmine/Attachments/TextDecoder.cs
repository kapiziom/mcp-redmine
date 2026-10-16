using System.Text;

namespace McpRedmine.Redmine.Attachments;

public static class TextDecoder
{
    static TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>BOM-aware; UTF-8 when valid, otherwise Windows-1250 (the usual legacy encoding of Polish text files).</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1250).GetString(bytes);
        }
    }

    /// <summary>A text file with NUL bytes (outside UTF-16) is really binary.</summary>
    public static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return false;

        var sample = Math.Min(bytes.Length, 8192);
        return bytes.AsSpan(0, sample).Contains((byte)0);
    }
}
