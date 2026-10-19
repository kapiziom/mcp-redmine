using System.ComponentModel;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Redmine.Attachments;
using McpRedmine.Redmine.Redaction;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpRedmine.Tools;

public sealed class AttachmentTools(
    ITargetResolver targets,
    IRedmineClientFactory clients,
    ProjectScope scope,
    AttachmentStore store,
    AttachmentBytesCache cache)
{
    private const int DefaultChars = 40_000;
    private const int MaxChars = 100_000;
    private const int MaxEmbeddedImages = 5;

    [McpServerTool(Name = "get_attachment", ReadOnly = true)]
    [Description("Read a file attached to an issue. Word (.docx), Excel (.xlsx), PowerPoint (.pptx) and text files come back as text (Markdown tables for spreadsheets); png/jpeg/gif/webp screenshots come back as images; PDFs and other binaries are saved to disk and the path is returned. Use the attachment id from get_issue (include_attachments=true) and fetch only files you have not already read. Images pasted into a description as !name.png! are attachments of that issue too. Long text is paged with offset/max_chars.")]
    public async Task<CallToolResult> GetAttachment(
        [Description("Issue the attachment belongs to.")] int issue_id,
        [Description("Attachment id from get_issue.")] int attachment_id,
        [Description(ToolSupport.TargetDescription)] string? target = null,
        [Description("Character offset into the extracted text, for continuing a long document (use next_offset).")] int? offset = null,
        [Description("Max characters of text to return (default 40000, max 100000).")] int? max_chars = null,
        [Description("For docx/xlsx/pptx: also return up to 5 pictures embedded in the file (screenshots in a specification).")] bool include_embedded_images = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var client = clients.Create(resolved);

        var issue = await client.GetIssueAsync(issue_id, includeJournals: false, includeAttachments: true, cancellationToken);
        if (resolved.HasAllowlist && (issue.Project is null || !await scope.IsAllowedAsync(client, issue.Project.Id, cancellationToken)))
            throw ProjectScope.Outside(resolved, $"Issue #{issue_id}");

        var redactor = TextRedactor.For(resolved);
        var guard = InjectionGuard.For(resolved);
        redactor.AddKnownPeople([issue.Author, issue.AssignedTo, .. (issue.Attachments ?? []).Select(a => a.Author)], resolved.RedactPeople);

        var attachments = issue.Attachments ?? [];
        var attachment = attachments.FirstOrDefault(a => a.Id == attachment_id)
            ?? throw new UserFacingException(
                $"Attachment #{attachment_id} is not attached to issue #{issue_id}. "
                + (attachments.Count == 0 ? "This issue has no attachments." : "Attachments: " + string.Join(", ", attachments.Select(a => $"#{a.Id} {guard.Scan(redactor.Redact(a.Filename), "attachment name")}")) + "."));

        var filename = attachment.Filename ?? $"attachment-{attachment.Id}";
        if (attachment.Filesize > AttachmentReader.MaxDownloadBytes)
            throw new UserFacingException(
                $"'{filename}' is {attachment.Filesize / (1024 * 1024)} MB, above the {AttachmentReader.MaxDownloadBytes / (1024 * 1024)} MB limit, so it was not downloaded.");

        var cacheKey = $"{resolved.Name}:{attachment.Id}";
        var bytes = cache.Get(cacheKey);
        if (bytes is null)
        {
            // Redmine only serves the file under its exact name, so the name comes from the issue's own attachment list.
            bytes = await client.GetBytesAsync($"attachments/download/{attachment.Id}/{Uri.EscapeDataString(filename)}", AttachmentReader.MaxDownloadBytes, cancellationToken);
            cache.Set(cacheKey, bytes);
        }

        var content = AttachmentReader.Read(filename, attachment.ContentType, bytes);

        // The name shown to the model (and used on disk) is masked; the real one is only used for the download URL.
        var shownName = guard.Scan(redactor.Redact(filename), $"attachment #{attachment.Id} name") ?? filename;

        string? savedPath = null;
        if (content.NeedsFile)
            savedPath = store.Save(resolved.Name, attachment.Id, shownName, bytes);

        var blocks = new List<ContentBlock>();
        var notes = new List<string>();
        if (content.Note is not null)
            notes.Add(content.Note);

        var start = Math.Max(0, offset ?? 0);
        var length = Math.Clamp(max_chars ?? DefaultChars, 1000, MaxChars);
        string? slice = null;
        var totalChars = 0;
        var hasMore = false;
        // Masked before paging so that offsets and numbering stay the same from one call to the next.
        if (guard.Scan(redactor.Redact(content.Text), "attachment text") is { } text)
        {
            totalChars = text.Length;
            if (start >= text.Length && text.Length > 0)
                throw new UserFacingException($"offset {start} is past the end of the text ({text.Length} characters).");
            slice = text.Substring(start, Math.Min(length, text.Length - start));
            hasMore = start + slice.Length < text.Length;
        }

        if (content.HiddenSkipped > 0)
            guard.AddWarning($"attachment: {content.HiddenSkipped} hidden part(s) (hidden text, rows, columns, sheets or slides) were ignored");

        var embedded = new List<EmbeddedImage>();
        var embeddedTotal = 0;
        if (AttachmentReader.MediaFolder(content.Kind) is { } folder && content.Kind != AttachmentKind.Other && AttachmentKinds.IsZip(bytes))
        {
            try
            {
                embedded = [.. OfficeText.Media(bytes, folder, AttachmentReader.MaxInlineImageBytes, include_embedded_images ? MaxEmbeddedImages : 0, out embeddedTotal)];
            }
            catch (UserFacingException ex)
            {
                notes.Add(ex.Message);
            }

            if (embeddedTotal > 0 && !include_embedded_images)
                notes.Add($"The file contains {embeddedTotal} embedded picture(s) that were not returned; pass include_embedded_images=true to get up to {MaxEmbeddedImages}.");
            else if (embeddedTotal > embedded.Count)
                notes.Add($"Only the first {embedded.Count} of {embeddedTotal} embedded pictures were returned.");
        }

        var wrappedSlice = slice is null ? null : guard.Guard(slice, "attachment text");

        var meta = new
        {
            target = resolved.Name,
            issue_id,
            attachment = new
            {
                attachment.Id,
                filename = shownName,
                size = bytes.Length,
                content_type = attachment.ContentType,
                kind = AttachmentKinds.Name(content.Kind),
                author = PersonRedactor.Format(attachment.Author, resolved.RedactPeople),
                created_on = attachment.CreatedOn,
            },
            text_characters = content.Text is null ? (int?)null : totalChars,
            offset = content.Text is null ? (int?)null : start,
            returned_characters = slice?.Length,
            has_more = content.Text is null ? (bool?)null : hasMore,
            next_offset = hasMore ? start + slice!.Length : (int?)null,
            saved_path = savedPath,
            embedded_pictures = embeddedTotal > 0 ? embeddedTotal : (int?)null,
            note = notes.Count == 0 ? null : string.Join(" ", notes),
            untrusted_text_notice = ToolSupport.SecurityNotice(guard),
            security_warnings = ToolSupport.SecurityWarnings(guard),
        };

        blocks.Add(new TextContentBlock { Text = ToolJson.Serialize(meta) });
        if (slice is not null)
            blocks.Add(new TextContentBlock { Text = wrappedSlice! });
        if (content.Image is { } image)
            blocks.Add(ImageContentBlock.FromBytes(image.Data, image.MimeType));
        foreach (var picture in embedded)
            blocks.Add(ImageContentBlock.FromBytes(picture.Data, picture.MimeType));

        return new CallToolResult { Content = blocks };
    }
}
