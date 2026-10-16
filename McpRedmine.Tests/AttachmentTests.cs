using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Redmine.Attachments;
using McpRedmine.Tests.TestSupport;
using McpRedmine.Tools;
using ModelContextProtocol.Protocol;

namespace McpRedmine.Tests;

public class PersonRedactorTests
{
    [Theory]
    [InlineData("Jan Kowalski", "J.K.")]
    [InlineData("anna nowak-kowalska", "A.N.K.")]
    [InlineData("  Łukasz   Żółć ", "Ł.Ż.")]
    [InlineData("bot", "B.")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Names_become_initials(string? name, string expected) =>
        Assert.Equal(expected, PersonRedactor.Initials(name));

    [Fact]
    public void The_id_stays_so_people_remain_distinguishable()
    {
        Assert.Equal("J.K. (#12)", PersonRedactor.Format(new RedmineIdName(12, "Jan Kowalski"), redact: true));
        Assert.Equal("Jan Kowalski", PersonRedactor.Format(new RedmineIdName(12, "Jan Kowalski"), redact: false));
        Assert.Null(PersonRedactor.Format(null, redact: true));
    }

    [Fact]
    public void Redaction_is_on_by_default_and_configurable()
    {
        Assert.True(Targets.Make().RedactPeople);
        var json = """{ "targets": [ { "name": "a", "url": "https://r.example.com", "redact_people": false } ] }""";
        var dir = Path.Combine(Path.GetTempPath(), "rmcp-red-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "c.json"), json);
            Assert.False(ConfigStore.Load(Path.Combine(dir, "c.json")).Targets[0].RedactPeople);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class OfficeTextTests
{
    [Fact]
    public void Docx_keeps_headings_lists_tables_and_drops_deleted_text()
    {
        var body = Ooxml.Paragraph("Requirements", style: "Heading1")
                   + Ooxml.Paragraph("The user must log in.")
                   + Ooxml.Paragraph("sub item", listLevel: 1)
                   + Ooxml.Table(["Field", "Type"], ["Name", "string"])
                   + """<w:p><w:del><w:r><w:delText>REMOVED</w:delText></w:r></w:del><w:r><w:t>kept</w:t></w:r></w:p>""";

        var text = OfficeText.Docx(Ooxml.Docx(body));

        Assert.Contains("# Requirements", text);
        Assert.Contains("The user must log in.", text);
        Assert.Contains("  - sub item", text);
        Assert.Contains("| Field | Type |", text);
        Assert.Contains("| --- | --- |", text);
        Assert.Contains("| Name | string |", text);
        Assert.Contains("kept", text);
        Assert.DoesNotContain("REMOVED", text);
    }

    [Fact]
    public void Docx_text_boxes_are_read_once_not_twice()
    {
        const string textBox = """
            <w:p><mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">
              <mc:Choice><w:r><w:t>BOXED</w:t></w:r></mc:Choice>
              <mc:Fallback><w:r><w:t>BOXED</w:t></w:r></mc:Fallback>
            </mc:AlternateContent></w:p>
            """;

        var text = OfficeText.Docx(Ooxml.Docx(textBox));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "BOXED"));
    }

    [Fact]
    public void Xlsx_renders_visible_sheets_as_tables_with_dates_and_shared_strings()
    {
        var text = OfficeText.Xlsx(Ooxml.Xlsx());

        Assert.Contains("## Sheet: Plan", text);
        Assert.Contains("| Task | Due |", text);
        Assert.Contains("| Rich text | 2023-03-15 |", text);   // rich-text shared string joined, date style applied
        Assert.Contains("| 0.3 | TRUE |", text);               // float noise trimmed, boolean spelled out
        Assert.Contains("a\\|b", text);                        // pipe escaped so the table stays valid
        Assert.Contains("| 2 |", text);                        // formula shows its cached value
        Assert.DoesNotContain("TOP-SECRET-CELL", text);        // hidden sheet is not exposed
        Assert.Contains("Secret (hidden)", text);
    }

    [Fact]
    public void Pptx_lists_slides_in_numeric_order()
    {
        var text = OfficeText.Pptx(Ooxml.Pptx("First", "Second", "Third"));

        Assert.True(text.IndexOf("First", StringComparison.Ordinal) < text.IndexOf("Second", StringComparison.Ordinal));
        Assert.Contains("## Slide 3", text);
    }

    [Fact]
    public void Embedded_pictures_are_found_and_validated_by_content()
    {
        var docx = Ooxml.Docx(Ooxml.Paragraph("x"),
            ("word/media/image1.png", Ooxml.Png), ("word/media/image2.png", Ooxml.Png), ("word/media/fake.png", "not an image"u8.ToArray()));

        var images = OfficeText.Media(docx, "word/media/", 1024 * 1024, maxCount: 1, out var total);

        Assert.Equal(2, total);          // the fake one does not count
        Assert.Single(images);           // capped by maxCount
        Assert.Equal("image/png", images[0].MimeType);
    }

    [Fact]
    public void A_non_zip_is_reported_not_crashed_on() =>
        Assert.Throws<UserFacingException>(() => OfficeText.Docx("%PDF-1.4"u8.ToArray()));

    [Fact]
    public void External_entities_in_the_xml_are_refused()
    {
        var evil = Ooxml.Zip([("word/document.xml", Encoding.UTF8.GetBytes(
            $"""<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><w:document xmlns:w="{Ooxml.WNs}"><w:body><w:p><w:r><w:t>&e;</w:t></w:r></w:p></w:body></w:document>"""))]);

        var ex = Assert.Throws<UserFacingException>(() => OfficeText.Docx(evil));
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public void A_decompression_bomb_is_refused()
    {
        var bomb = Ooxml.Zip([("word/document.xml", Encoding.UTF8.GetBytes(
            $"""<w:document xmlns:w="{Ooxml.WNs}"><w:body>{new string(' ', OfficeText.MaxPartBytes + 1024)}</w:body></w:document>"""))]);

        Assert.Throws<UserFacingException>(() => OfficeText.Docx(bomb));
    }
}

public class AttachmentReaderTests
{
    [Theory]
    [InlineData("spec.DOCX", null, AttachmentKind.Docx)]
    [InlineData("plan.xlsx", null, AttachmentKind.Xlsx)]
    [InlineData("deck.pptx", null, AttachmentKind.Pptx)]
    [InlineData("shot.PNG", null, AttachmentKind.Image)]
    [InlineData("notes.csv", null, AttachmentKind.Text)]
    [InlineData("noext", "text/plain", AttachmentKind.Text)]
    [InlineData("scan.pdf", null, AttachmentKind.Pdf)]
    [InlineData("old.doc", "application/msword", AttachmentKind.Other)]
    [InlineData("data.bin", null, AttachmentKind.Other)]
    public void Files_are_classified_by_name_then_content_type(string name, string? contentType, AttachmentKind expected) =>
        Assert.Equal(expected, AttachmentKinds.Classify(name, contentType));

    [Fact]
    public void A_real_image_is_returned_inline_with_its_detected_mime()
    {
        var content = AttachmentReader.Read("shot.png", "image/png", Ooxml.Png);

        Assert.Equal("image/png", content.Image!.MimeType);
        Assert.False(content.NeedsFile);
    }

    [Fact]
    public void A_file_named_like_an_image_but_not_one_is_not_trusted()
    {
        var content = AttachmentReader.Read("shot.png", "image/png", "<html>nope</html>"u8.ToArray());

        Assert.Null(content.Image);
        Assert.True(content.NeedsFile);
        Assert.Contains("not a supported image", content.Note);
    }

    [Fact]
    public void An_oversized_image_goes_to_disk_instead_of_the_context()
    {
        var big = Ooxml.Png.Concat(new byte[5 * 1024 * 1024]).ToArray();

        var content = AttachmentReader.Read("big.png", "image/png", big);

        Assert.Null(content.Image);
        Assert.True(content.NeedsFile);
        Assert.Contains("inline limit", content.Note);
    }

    [Fact]
    public void Text_is_decoded_as_utf8_or_windows_1250()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var utf8 = AttachmentReader.Read("a.txt", null, "zażółć gęślą"u8.ToArray());
        var legacy = AttachmentReader.Read("b.txt", null, Encoding.GetEncoding(1250).GetBytes("zażółć gęślą"));
        var withBom = AttachmentReader.Read("c.txt", null, [.. Encoding.UTF8.GetPreamble(), .. "zażółć"u8.ToArray()]);

        Assert.Equal("zażółć gęślą", utf8.Text);
        Assert.Equal("zażółć gęślą", legacy.Text);
        Assert.Equal("zażółć", withBom.Text);
    }

    [Fact]
    public void A_binary_file_with_a_text_extension_is_not_dumped_as_text()
    {
        var content = AttachmentReader.Read("data.txt", null, [1, 2, 0, 0, 3, 4]);

        Assert.Null(content.Text);
        Assert.True(content.NeedsFile);
    }

    [Fact]
    public void Pdf_and_unknown_types_need_a_file()
    {
        Assert.True(AttachmentReader.Read("a.pdf", null, "%PDF-1.7"u8.ToArray()).NeedsFile);
        Assert.True(AttachmentReader.Read("a.doc", null, [0xD0, 0xCF, 0x11, 0xE0]).NeedsFile);
    }

    [Fact]
    public void A_corrupt_docx_degrades_to_a_file_with_an_explanation()
    {
        var content = AttachmentReader.Read("spec.docx", null, "plainly not a zip"u8.ToArray());

        Assert.True(content.NeedsFile);
        Assert.Contains("original was saved", content.Note);
    }
}

public sealed class AttachmentToolsTests : IDisposable
{
    private const string IssueJson = """
        {"issue":{"id":42,"subject":"S","project":{"id":1,"name":"Alpha"},"attachments":[
          {"id":9,"filename":"spec v2.docx","filesize":1000,"content_type":"application/octet-stream","author":{"id":5,"name":"Anna Nowak"},"created_on":"2025-01-01T00:00:00Z"},
          {"id":10,"filename":"shot.png","filesize":90,"content_type":"image/png","author":{"id":5,"name":"Anna Nowak"}},
          {"id":11,"filename":"report.pdf","filesize":50,"content_type":"application/pdf","author":{"id":6,"name":"Bob Builder"}},
          {"id":12,"filename":"notes.txt","filesize":3000,"content_type":"text/plain"},
          {"id":13,"filename":"huge.zip","filesize":99999999999,"content_type":"application/zip"},
          {"id":14,"filename":"login.html","filesize":10,"content_type":"text/html"}]}}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-att-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, byte[]> _files = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FixedFactory(RedmineClient client) : IRedmineClientFactory
    {
        public RedmineClient Create(TargetConfig target) => client;
    }

    private (AttachmentTools Tools, StubHandler Handler) Make(TargetConfig? target = null, string issueJson = IssueJson)
    {
        target ??= Targets.Make("main");
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/issues/42.json", StringComparison.Ordinal))
                return StubHandler.Json(issueJson);

            var segments = path.Split('/');
            if (path.Contains("/attachments/download/", StringComparison.Ordinal) && int.TryParse(segments[^2], out var id) && _files.TryGetValue(id, out var bytes))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(id == 14 ? "text/html" : "application/octet-stream");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var tools = new AttachmentTools(
            new TargetResolver(new RedmineMcpConfig { Targets = [target] }),
            new FixedFactory(Targets.Client(handler, target)),
            new ProjectScope(),
            new AttachmentStore(Path.Combine(_dir, "store")),
            new AttachmentBytesCache());
        return (tools, handler);
    }

    private static JsonElement Meta(CallToolResult result) => JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text).RootElement;

    private static int Downloads(StubHandler handler) =>
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.Contains("/attachments/download/", StringComparison.Ordinal));

    [Fact]
    public async Task A_word_spec_comes_back_as_text_with_the_exact_download_path_and_initials_for_the_author()
    {
        _files[9] = Ooxml.Docx(Ooxml.Paragraph("Requirements", style: "Heading1") + Ooxml.Paragraph("Must log in."));
        var (tools, handler) = Make();

        var result = await tools.GetAttachment(42, 9);

        var meta = Meta(result);
        Assert.Equal("docx", meta.GetProperty("attachment").GetProperty("kind").GetString());
        Assert.Equal("A.N. (#5)", meta.GetProperty("attachment").GetProperty("author").GetString());
        Assert.DoesNotContain("Anna", ((TextContentBlock)result.Content[0]).Text);
        Assert.Contains("# Requirements", ((TextContentBlock)result.Content[1]).Text);
        var download = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.Contains("/attachments/download/", StringComparison.Ordinal));
        Assert.EndsWith("/attachments/download/9/spec%20v2.docx", download.RequestUri!.AbsolutePath.Replace(" ", "%20"), StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(download.RequestUri.Query));
    }

    [Fact]
    public async Task A_screenshot_comes_back_as_an_image_block()
    {
        _files[10] = Ooxml.Png;
        var (tools, _) = Make();

        var result = await tools.GetAttachment(42, 10);

        var image = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content, c => c is ImageContentBlock));
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Ooxml.Png, image.DecodedData.ToArray());
        Assert.Equal("image", Meta(result).GetProperty("attachment").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_pdf_is_saved_and_its_path_returned()
    {
        _files[11] = "%PDF-1.7 content"u8.ToArray();
        var (tools, _) = Make();

        var result = await tools.GetAttachment(42, 11);

        var path = Meta(result).GetProperty("saved_path").GetString()!;
        Assert.StartsWith(_dir, path, StringComparison.Ordinal);
        Assert.Equal(_files[11], await File.ReadAllBytesAsync(path));
        Assert.Single(result.Content); // metadata only, nothing else to put in the context
    }

    [Fact]
    public async Task Long_text_is_paged_with_offset()
    {
        _files[12] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => (char)('a' + (i % 26)))));
        var (tools, _) = Make();

        var first = await tools.GetAttachment(42, 12, max_chars: 1000);
        var second = await tools.GetAttachment(42, 12, offset: Meta(first).GetProperty("next_offset").GetInt32(), max_chars: 1000);

        Assert.True(Meta(first).GetProperty("has_more").GetBoolean());
        Assert.Equal(3000, Meta(first).GetProperty("text_characters").GetInt32());
        Assert.Equal(1000, ((TextContentBlock)second.Content[1]).Text.Length);
        Assert.Equal(2000, Meta(second).GetProperty("next_offset").GetInt32());
        await Assert.ThrowsAsync<UserFacingException>(() => tools.GetAttachment(42, 12, offset: 99999));
    }

    [Fact]
    public async Task Repeated_reads_do_not_download_again()
    {
        _files[12] = "hello"u8.ToArray();
        var (tools, handler) = Make();

        await tools.GetAttachment(42, 12);
        await tools.GetAttachment(42, 12);

        Assert.Equal(1, Downloads(handler));
    }

    [Fact]
    public async Task An_attachment_of_another_issue_is_refused_with_the_valid_ids()
    {
        var (tools, handler) = Make();

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => tools.GetAttachment(42, 777));

        Assert.Contains("#9 spec v2.docx", ex.Message);
        Assert.Equal(0, Downloads(handler));
    }

    [Fact]
    public async Task Files_above_the_download_limit_are_not_downloaded()
    {
        var (tools, handler) = Make();

        await Assert.ThrowsAsync<UserFacingException>(() => tools.GetAttachment(42, 13));

        Assert.Equal(0, Downloads(handler));
    }

    [Fact]
    public async Task The_project_allowlist_applies_before_any_download()
    {
        _files[9] = Ooxml.Docx(Ooxml.Paragraph("secret"));
        var (tools, handler) = Make(Targets.Make("main") with { ProjectAllowlist = ["2"] });

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => tools.GetAttachment(42, 9));

        Assert.Contains("allowlist", ex.Message);
        Assert.Equal(0, Downloads(handler));
    }

    [Fact]
    public async Task A_login_page_instead_of_the_file_is_reported()
    {
        _files[14] = "<html>please log in</html>"u8.ToArray();
        var (tools, _) = Make();

        var ex = await Assert.ThrowsAsync<RedmineException>(() => tools.GetAttachment(42, 14));

        Assert.Equal(RedmineErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task Embedded_pictures_are_offered_and_returned_on_request()
    {
        _files[9] = Ooxml.Docx(Ooxml.Paragraph("see screenshot"), ("word/media/image1.png", Ooxml.Png));
        var (tools, _) = Make();

        var without = await tools.GetAttachment(42, 9);
        var with = await tools.GetAttachment(42, 9, include_embedded_images: true);

        Assert.DoesNotContain(without.Content, c => c is ImageContentBlock);
        Assert.Contains("include_embedded_images=true", Meta(without).GetProperty("note").GetString());
        Assert.Single(with.Content, c => c is ImageContentBlock);
    }

    [Fact]
    public async Task Real_names_are_shown_only_when_redaction_is_off()
    {
        _files[9] = Ooxml.Docx(Ooxml.Paragraph("x"));
        var (tools, _) = Make(Targets.Make("main") with { RedactPeople = false });

        var result = await tools.GetAttachment(42, 9);

        Assert.Equal("Anna Nowak", Meta(result).GetProperty("attachment").GetProperty("author").GetString());
    }
}

public class RedmineClientDownloadTests
{
    private static HttpResponseMessage Bytes(byte[] data, long? declaredLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (declaredLength is { } length)
            response.Content.Headers.ContentLength = length;
        return response;
    }

    [Fact]
    public async Task A_declared_length_above_the_limit_is_rejected_before_reading()
    {
        var client = Targets.Client(new StubHandler(_ => Bytes(new byte[10], declaredLength: 5000)));

        var ex = await Assert.ThrowsAsync<RedmineException>(() => client.GetBytesAsync("f", maxBytes: 1000));

        Assert.Equal(RedmineErrorKind.TooLarge, ex.Kind);
    }

    [Fact]
    public async Task A_body_that_outgrows_the_limit_is_cut_off_even_without_a_declared_length()
    {
        var client = Targets.Client(new StubHandler(_ => Bytes(new byte[5000])));

        await Assert.ThrowsAsync<RedmineException>(() => client.GetBytesAsync("f", maxBytes: 1000));
    }

    [Fact]
    public async Task A_small_file_is_returned_whole()
    {
        var client = Targets.Client(new StubHandler(_ => Bytes([1, 2, 3])));

        Assert.Equal([1, 2, 3], await client.GetBytesAsync("f", maxBytes: 1000));
    }

    [Fact]
    public async Task Missing_files_and_permission_problems_map_like_any_other_request()
    {
        var client = Targets.Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var ex = await Assert.ThrowsAsync<RedmineException>(() => client.GetBytesAsync("f", maxBytes: 1000));

        Assert.Equal(RedmineErrorKind.Forbidden, ex.Kind);
    }
}

public sealed class AttachmentStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("../../evil.txt")]
    [InlineData("..\\..\\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public void Hostile_file_names_cannot_escape_the_store(string filename)
    {
        var path = new AttachmentStore(_dir).Save("main", 1, filename, [1]);

        Assert.StartsWith(Path.GetFullPath(_dir), Path.GetFullPath(path), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_dir, "main"), Path.GetDirectoryName(path));
    }

    [Fact]
    public void Old_files_are_purged_when_new_ones_are_saved()
    {
        var store = new AttachmentStore(_dir);
        var old = store.Save("main", 1, "old.bin", [1]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - AttachmentStore.Retention - TimeSpan.FromDays(1));

        var fresh = store.Save("main", 2, "new.bin", [2]);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }
}
