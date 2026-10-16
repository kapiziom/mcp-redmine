using System.IO.Compression;
using System.Text;

namespace McpRedmine.Tests.TestSupport;

/// <summary>Builds minimal but structurally real .docx/.xlsx/.pptx packages for tests.</summary>
public static class Ooxml
{
    public const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string SNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>A real 1x1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return memory.ToArray();
    }

    private static (string, byte[]) Part(string name, string xml) => (name, Encoding.UTF8.GetBytes(xml));

    public static byte[] Docx(string bodyXml, params (string Name, byte[] Data)[] extra) =>
        Zip([Part("word/document.xml", $"""<?xml version="1.0"?><w:document xmlns:w="{WNs}"><w:body>{bodyXml}</w:body></w:document>"""), .. extra]);

    public static string Paragraph(string text, string? style = null, int? listLevel = null)
    {
        var properties = (style, listLevel) switch
        {
            (not null, _) => $"""<w:pPr><w:pStyle w:val="{style}"/></w:pPr>""",
            (_, not null) => $"""<w:pPr><w:numPr><w:ilvl w:val="{listLevel}"/><w:numId w:val="1"/></w:numPr></w:pPr>""",
            _ => string.Empty,
        };
        return $"<w:p>{properties}<w:r><w:t>{text}</w:t></w:r></w:p>";
    }

    public static string Table(params string[][] rows) =>
        "<w:tbl>" + string.Concat(rows.Select(row =>
            "<w:tr>" + string.Concat(row.Select(cell => $"<w:tc>{Paragraph(cell)}</w:tc>")) + "</w:tr>")) + "</w:tbl>";

    public static byte[] Pptx(params string[] slideTexts) =>
        Zip(slideTexts.Select((text, i) => Part($"ppt/slides/slide{i + 1}.xml",
            $"""<p:sld xmlns:p="p" xmlns:a="{ANs}"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>""")));

    /// <summary>One visible sheet "Plan" (sharedStrings, a date style, a boolean, a float) and one hidden sheet.</summary>
    public static byte[] Xlsx()
    {
        return Zip(
        [
            Part("xl/workbook.xml", $"""<workbook xmlns="{SNs}" xmlns:r="{RNs}"><sheets><sheet name="Plan" sheetId="1" r:id="rId1"/><sheet name="Secret" sheetId="2" state="hidden" r:id="rId2"/></sheets></workbook>"""),
            Part("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="worksheets/sheet2.xml"/></Relationships>"""),
            Part("xl/sharedStrings.xml", $"""<sst xmlns="{SNs}"><si><t>Task</t></si><si><t>Due</t></si><si><r><t>Rich </t></r><r><t>text</t></r></si></sst>"""),
            Part("xl/styles.xml", $"""<styleSheet xmlns="{SNs}"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs></styleSheet>"""),
            Part("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="{SNs}"><sheetData>
                  <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                  <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2" s="1"><v>45000</v></c></row>
                  <row r="3"><c r="A3"><v>0.30000000000000004</v></c><c r="B3" t="b"><v>1</v></c></row>
                  <row r="4"/>
                  <row r="5"><c r="A5" t="inlineStr"><is><t>a|b</t></is></c><c r="C5"><f>1+1</f><v>2</v></c></row>
                </sheetData></worksheet>
                """),
            Part("xl/worksheets/sheet2.xml", $"""<worksheet xmlns="{SNs}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>TOP-SECRET-CELL</t></is></c></row></sheetData></worksheet>"""),
        ]);
    }
}
