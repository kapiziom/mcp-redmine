using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using McpRedmine.Config;

namespace McpRedmine.Redmine.Attachments;

public sealed record EmbeddedImage(string Name, byte[] Data, string MimeType);

/// <summary>Extracted text plus how many hidden parts (hidden runs, rows, columns, slides) were left out on purpose.</summary>
public sealed record OfficeResult(string Text, int HiddenSkipped);

/// <summary>
/// Reads the text of Office Open XML files (.docx, .xlsx, .pptx) without any Office library: they are ZIP
/// archives of XML. Untrusted input, so DTDs are prohibited, every part is size-capped and nothing is executed.
/// </summary>
public static partial class OfficeText
{
    public const int MaxPartBytes = 40 * 1024 * 1024;
    public const int MaxSheets = 15;
    public const int MaxRowsPerSheet = 500;
    public const int MaxColumns = 40;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [GeneratedRegex(@"^(?:Heading|Nag[lł][oó]wek)\s*(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingStyle();

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex SlidePart();

    // ---- Word -----------------------------------------------------------------------------------------------

    private sealed class HiddenCounter
    {
        public int Count;
    }

    public static string Docx(byte[] data) => ReadDocx(data).Text;

    /// <summary>Text hidden with the "vanish" font option is skipped: people cannot see it, so it is a classic place to hide instructions.</summary>
    public static OfficeResult ReadDocx(byte[] data)
    {
        using var zip = OpenZip(data);
        var document = LoadXml(zip, "word/document.xml") ?? throw Corrupt();
        var body = document.Root?.Element(W + "body") ?? throw Corrupt();

        var hidden = new HiddenCounter();
        var text = new StringBuilder();
        foreach (var element in body.Elements())
            WriteBlock(element, text, hidden);
        return new OfficeResult(text.ToString().Trim(), hidden.Count);
    }

    private static void WriteBlock(XElement element, StringBuilder text, HiddenCounter hidden)
    {
        if (element.Name == W + "p")
        {
            var line = ParagraphText(element, hidden);
            if (line.Length > 0)
                text.Append(line).Append("\n\n");
        }
        else if (element.Name == W + "tbl")
        {
            WriteTable(element, text, hidden);
        }
        else if (element.Name == W + "sdt")
        {
            foreach (var child in element.Element(W + "sdtContent")?.Elements() ?? [])
                WriteBlock(child, text, hidden);
        }
    }

    private static string ParagraphText(XElement paragraph, HiddenCounter hidden)
    {
        var content = new StringBuilder();
        Walk(paragraph, content, hidden);
        var line = content.ToString().Trim();
        if (line.Length == 0)
            return string.Empty;

        var properties = paragraph.Element(W + "pPr");
        var style = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        if (style is not null)
        {
            if (HeadingStyle().Match(style) is { Success: true } heading)
                return new string('#', Math.Clamp(int.Parse(heading.Groups[1].Value, CultureInfo.InvariantCulture), 1, 6)) + " " + line;
            if (style.Equals("Title", StringComparison.OrdinalIgnoreCase) || style.Equals("Tytul", StringComparison.OrdinalIgnoreCase))
                return "# " + line;
        }

        if (properties?.Element(W + "numPr") is { } numbering)
        {
            var level = int.TryParse(numbering.Element(W + "ilvl")?.Attribute(W + "val")?.Value, out var parsed) ? Math.Clamp(parsed, 0, 8) : 0;
            return new string(' ', level * 2) + "- " + line;
        }

        return line;
    }

    private static bool IsHiddenRun(XElement run)
    {
        var vanish = run.Element(W + "rPr")?.Element(W + "vanish");
        return vanish is not null && vanish.Attribute(W + "val")?.Value is not ("0" or "false" or "off");
    }

    private static void Walk(XElement element, StringBuilder text, HiddenCounter hidden)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name == W + "r" && IsHiddenRun(child))
                hidden.Count++;
            else if (child.Name == W + "t")
                text.Append(child.Value);
            else if (child.Name == W + "tab")
                text.Append('\t');
            else if (child.Name == W + "br" || child.Name == W + "cr")
                text.Append('\n');
            else if (child.Name == W + "noBreakHyphen")
                text.Append('-');
            else if (child.Name == W + "pPr" || child.Name == W + "rPr" || child.Name == W + "delText" || child.Name == Mc + "Fallback")
                continue; // formatting, deleted tracked-change text, and the duplicate fallback of drawings/text boxes
            else if (child.Name == W + "p")
                Walk(child, text.Append('\n'), hidden); // a paragraph nested in a text box
            else
                Walk(child, text, hidden);
        }
    }

    private static void WriteTable(XElement table, StringBuilder text, HiddenCounter hidden)
    {
        var rows = new List<List<string>>();
        foreach (var row in table.Elements(W + "tr"))
        {
            var cells = row.Elements(W + "tc")
                .Select(cell => string.Join(" / ", cell.Elements(W + "p").Select(p => Flatten(ParagraphText(p, hidden))).Where(t => t.Length > 0)))
                .ToList();
            if (cells.Any(c => c.Length > 0))
                rows.Add(cells);
        }

        AppendMarkdownTable(rows, text);
        text.Append('\n');
    }

    // ---- Excel ----------------------------------------------------------------------------------------------

    public static string Xlsx(byte[] data) => ReadXlsx(data).Text;

    /// <summary>Hidden sheets, rows and columns are left out (and counted): readers of the file do not see them either.</summary>
    public static OfficeResult ReadXlsx(byte[] data)
    {
        using var zip = OpenZip(data);
        var workbook = LoadXml(zip, "xl/workbook.xml") ?? throw Corrupt();

        var relationships = (LoadXml(zip, "xl/_rels/workbook.xml.rels")?.Root?.Elements(Pr + "Relationship") ?? [])
            .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null)
            .ToDictionary(r => r.Attribute("Id")!.Value, r => r.Attribute("Target")!.Value);
        var shared = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);
        var date1904 = workbook.Root?.Element(S + "workbookPr")?.Attribute("date1904")?.Value is "1" or "true";

        var output = new StringBuilder();
        var skipped = new List<string>();
        var hidden = new HiddenCounter();
        var sheetCount = 0;

        foreach (var sheet in workbook.Root?.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            var name = sheet.Attribute("name")?.Value ?? "Sheet";
            if (sheet.Attribute("state")?.Value is "hidden" or "veryHidden")
            {
                skipped.Add(name + " (hidden)");
                hidden.Count++;
                continue;
            }

            if (++sheetCount > MaxSheets)
            {
                skipped.Add(name);
                continue;
            }

            var relationshipId = sheet.Attribute(R + "id")?.Value;
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var target))
                continue;

            var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
            output.Append("## Sheet: ").Append(name).Append("\n\n");
            output.Append(RenderSheet(zip, path, shared, dateStyles, date1904, hidden)).Append('\n');
        }

        if (skipped.Count > 0)
            output.Append("(not shown: ").Append(string.Join(", ", skipped)).Append(")\n");

        return new OfficeResult(output.ToString().Trim(), hidden.Count);
    }

    private static string RenderSheet(ZipArchive zip, string path, List<string> shared, HashSet<int> dateStyles, bool date1904, HiddenCounter hidden)
    {
        var entry = zip.GetEntry(path);
        if (entry is null)
            return "(sheet data missing)\n";

        var rows = new List<List<string>>();
        var hiddenColumns = new HashSet<int>();
        var truncated = false;

        using var stream = OpenEntry(entry);
        using var reader = XmlReader.Create(stream, SafeXml());
        reader.MoveToContent();

        // XNode.ReadFrom leaves the reader on the node AFTER the element, so Read() is only called when nothing was consumed;
        // otherwise every second row of a compactly written sheet would be skipped.
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            if (reader.LocalName == "col")
            {
                if (reader.GetAttribute("hidden") is "1" or "true"
                    && int.TryParse(reader.GetAttribute("min"), out var first) && int.TryParse(reader.GetAttribute("max"), out var last))
                {
                    for (var column = first - 1; column <= Math.Min(last - 1, MaxColumns - 1); column++)
                        hiddenColumns.Add(column);
                }

                reader.Read();
                continue;
            }

            if (reader.LocalName != "row")
            {
                reader.Read();
                continue;
            }

            if (rows.Count >= MaxRowsPerSheet)
            {
                truncated = true;
                break;
            }

            var row = (XElement)XNode.ReadFrom(reader);
            if (row.Attribute("hidden")?.Value is "1" or "true")
            {
                hidden.Count++;
                continue;
            }

            var cells = new SortedDictionary<int, string>();
            foreach (var cell in row.Elements(S + "c"))
            {
                var column = ColumnIndex(cell.Attribute("r")?.Value);
                if (column < 0 || column >= MaxColumns)
                    continue;
                var value = CellValue(cell, shared, dateStyles, date1904);
                if (value.Length == 0)
                    continue;
                if (hiddenColumns.Contains(column))
                {
                    hidden.Count++;
                    continue;
                }

                cells[column] = value;
            }

            if (cells.Count == 0)
                continue;

            var width = cells.Keys.Max() + 1;
            rows.Add(Enumerable.Range(0, width).Select(i => cells.GetValueOrDefault(i, string.Empty)).ToList());
        }

        var text = new StringBuilder();
        AppendMarkdownTable(rows, text);
        if (rows.Count == 0)
            text.Append("(empty)\n");
        if (truncated)
            text.Append(CultureInfo.InvariantCulture, $"(only the first {MaxRowsPerSheet} non-empty rows are shown)\n");
        return text.ToString();
    }

    private static string CellValue(XElement cell, List<string> shared, HashSet<int> dateStyles, bool date1904)
    {
        var type = cell.Attribute("t")?.Value;
        var raw = cell.Element(S + "v")?.Value;

        switch (type)
        {
            case "s":
                return int.TryParse(raw, out var index) && index >= 0 && index < shared.Count ? Flatten(shared[index]) : string.Empty;
            case "inlineStr":
                return Flatten(string.Concat(cell.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []));
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            case "str" or "e" or "d":
                return Flatten(raw ?? string.Empty);
        }

        if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Flatten(raw ?? string.Empty);

        var style = int.TryParse(cell.Attribute("s")?.Value, out var s) ? s : 0;
        if (dateStyles.Contains(style) && TryDate(number, date1904, out var date))
            return date;

        return number.ToString("G15", CultureInfo.InvariantCulture);
    }

    private static bool TryDate(double serial, bool date1904, out string text)
    {
        text = string.Empty;
        try
        {
            var value = DateTime.FromOADate(serial + (date1904 ? 1462 : 0));
            text = serial < 1 ? value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : serial % 1 == 0 ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var document = LoadXml(zip, "xl/sharedStrings.xml");
        return (document?.Root?.Elements(S + "si") ?? [])
            // <rPh> is the phonetic guide (furigana), not part of the text.
            .Select(si => string.Concat(si.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value)))
            .ToList();
    }

    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        var styles = LoadXml(zip, "xl/styles.xml")?.Root;
        if (styles is null)
            return result;

        var customFormats = (styles.Element(S + "numFmts")?.Elements(S + "numFmt") ?? [])
            .Where(f => f.Attribute("numFmtId") is not null)
            .ToDictionary(f => int.Parse(f.Attribute("numFmtId")!.Value, CultureInfo.InvariantCulture), f => f.Attribute("formatCode")?.Value ?? string.Empty);

        var position = 0;
        foreach (var xf in styles.Element(S + "cellXfs")?.Elements(S + "xf") ?? [])
        {
            var id = int.TryParse(xf.Attribute("numFmtId")?.Value, out var parsed) ? parsed : 0;
            if (IsDateFormat(id, customFormats))
                result.Add(position);
            position++;
        }

        return result;
    }

    private static bool IsDateFormat(int id, Dictionary<int, string> custom)
    {
        if (id is (>= 14 and <= 22) or (>= 27 and <= 36) or (>= 45 and <= 47) or (>= 50 and <= 58))
            return true;
        if (!custom.TryGetValue(id, out var code))
            return false;

        var stripped = QuotedOrBracketed().Replace(code, string.Empty);
        return stripped.Any(c => char.ToLowerInvariant(c) is 'y' or 'd' or 'h' or 's' || c is 'm' or 'M');
    }

    [GeneratedRegex("\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.")]
    private static partial Regex QuotedOrBracketed();

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return -1;

        var column = 0;
        foreach (var c in reference)
        {
            if (!char.IsAsciiLetter(c))
                break;
            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return column - 1;
    }

    // ---- PowerPoint -----------------------------------------------------------------------------------------

    public static string Pptx(byte[] data) => ReadPptx(data).Text;

    /// <summary>Slides marked as hidden are left out (and counted).</summary>
    public static OfficeResult ReadPptx(byte[] data)
    {
        using var zip = OpenZip(data);
        var slides = zip.Entries
            .Select(e => (Entry: e, Match: SlidePart().Match(e.FullName)))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
        if (slides.Count == 0)
            throw Corrupt();

        var hiddenSlides = 0;
        var text = new StringBuilder();
        foreach (var (entry, match) in slides)
        {
            var document = LoadEntry(entry);
            if (document.Root?.Attribute("show")?.Value == "0")
            {
                hiddenSlides++;
                continue;
            }

            text.Append("## Slide ").Append(match.Groups[1].Value).Append("\n\n");
            foreach (var paragraph in document.Descendants(A + "p"))
            {
                var line = new StringBuilder();
                foreach (var node in paragraph.Descendants())
                {
                    if (node.Name == A + "t")
                        line.Append(node.Value);
                    else if (node.Name == A + "br")
                        line.Append(' ');
                }

                if (line.ToString().Trim() is { Length: > 0 } content)
                    text.Append(content).Append('\n');
            }

            text.Append('\n');
        }

        return new OfficeResult(text.ToString().Trim(), hiddenSlides);
    }

    // ---- shared ---------------------------------------------------------------------------------------------

    /// <summary>Pictures stored inside the file (<c>word/media</c>, <c>xl/media</c>, <c>ppt/media</c>), in name order.</summary>
    public static IReadOnlyList<EmbeddedImage> Media(byte[] data, string mediaFolder, long maxBytesEach, int maxCount, out int total)
    {
        using var zip = OpenZip(data);
        var candidates = zip.Entries
            .Where(e => e.FullName.StartsWith(mediaFolder, StringComparison.OrdinalIgnoreCase) && e.Length > 0)
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var images = new List<EmbeddedImage>();
        var supported = 0;
        foreach (var entry in candidates)
        {
            if (entry.Length > maxBytesEach)
                continue;

            using var stream = OpenEntry(entry);
            var bytes = ReadCapped(stream, maxBytesEach);
            if (AttachmentKinds.ImageMime(bytes) is not { } mime)
                continue;

            supported++;
            if (images.Count < maxCount)
                images.Add(new EmbeddedImage(Path.GetFileName(entry.FullName), bytes, mime));
        }

        total = supported;
        return images;
    }

    private static ZipArchive OpenZip(byte[] data)
    {
        if (!AttachmentKinds.IsZip(data))
            throw new UserFacingException("The file is not a valid Office Open XML document (it may be password-protected or in a legacy format).");

        try
        {
            return new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new UserFacingException("The file is damaged: its ZIP structure cannot be read.", ex);
        }
    }

    private static XDocument? LoadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        return entry is null ? null : LoadEntry(entry);
    }

    private static XDocument LoadEntry(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = OpenEntry(entry);
            using var reader = XmlReader.Create(stream, SafeXml());
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new UserFacingException($"The file is damaged: '{entry.FullName}' is not valid XML.", ex);
        }
    }

    private static Stream OpenEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPartBytes)
            throw new UserFacingException($"Part '{entry.FullName}' is too large to read safely.");

        return new CappedStream(entry.Open(), MaxPartBytes);
    }

    private static XmlReaderSettings SafeXml() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = MaxPartBytes,
    };

    private static byte[] ReadCapped(Stream stream, long max)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.Length > max ? throw new UserFacingException("Embedded image is too large.") : buffer.ToArray();
    }

    private static UserFacingException Corrupt() => new("The file does not look like a valid Office document (main part missing).");

    private static string Flatten(string text) =>
        text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Replace('\t', ' ').Trim();

    private static void AppendMarkdownTable(List<List<string>> rows, StringBuilder text)
    {
        if (rows.Count == 0)
            return;

        var width = Math.Min(MaxColumns, rows.Max(r => r.Count));
        string Row(List<string> cells) =>
            "| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => i < cells.Count ? cells[i].Replace("|", "\\|", StringComparison.Ordinal) : string.Empty)) + " |";

        text.Append(Row(rows[0])).Append('\n');
        text.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
        foreach (var row in rows.Skip(1))
            text.Append(Row(row)).Append('\n');
    }

    /// <summary>Stops decompression bombs: a part may expand to at most <c>limit</c> bytes whatever its header claims.</summary>
    private sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new UserFacingException("The document expands to an unreasonable size and was not read.") : read;
        }
    }
}
