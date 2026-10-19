using McpRedmine.Config;

namespace McpRedmine.Redmine.Attachments;

public sealed record ImagePayload(byte[] Data, string MimeType);

/// <summary>What could be made of an attachment: text, an image, or neither (then it is saved to disk instead).</summary>
public sealed record AttachmentContent(AttachmentKind Kind, string? Text, ImagePayload? Image, string? Note, int HiddenSkipped = 0)
{
    public bool NeedsFile => Text is null && Image is null;
}

public static class AttachmentReader
{
    /// <summary>Largest attachment that is downloaded at all.</summary>
    public const long MaxDownloadBytes = 25L * 1024 * 1024;

    /// <summary>Largest image sent inline to the model; bigger ones are saved to disk.</summary>
    public const long MaxInlineImageBytes = 4L * 1024 * 1024;

    public static AttachmentContent Read(string filename, string? contentType, byte[] bytes)
    {
        var kind = AttachmentKinds.Classify(filename, contentType);

        switch (kind)
        {
            case AttachmentKind.Image:
                return ReadImage(bytes);

            case AttachmentKind.Docx or AttachmentKind.Xlsx or AttachmentKind.Pptx:
                return ReadOffice(kind, bytes);

            case AttachmentKind.Text:
                return TextDecoder.LooksBinary(bytes)
                    ? new AttachmentContent(AttachmentKind.Other, null, null, "The file looks binary, not text.")
                    : new AttachmentContent(kind, TextDecoder.Decode(bytes), null, null);

            case AttachmentKind.Pdf:
                return new AttachmentContent(kind, null, null, "PDF is not converted here; open the saved file with a PDF-capable reader.");

            default:
                return new AttachmentContent(kind, null, null, "No text can be extracted from this file type; the original was saved.");
        }
    }

    private static AttachmentContent ReadImage(byte[] bytes)
    {
        var mime = AttachmentKinds.ImageMime(bytes);
        if (mime is null)
            return new AttachmentContent(AttachmentKind.Other, null, null, "The file is named like an image but its content is not a supported image (png, jpeg, gif, webp).");

        if (bytes.Length > MaxInlineImageBytes)
            return new AttachmentContent(AttachmentKind.Image, null, null,
                $"The image is {bytes.Length / (1024.0 * 1024.0):0.0} MB, above the {MaxInlineImageBytes / (1024 * 1024)} MB inline limit; the original was saved.");

        return new AttachmentContent(AttachmentKind.Image, null, new ImagePayload(bytes, mime), null);
    }

    private static AttachmentContent ReadOffice(AttachmentKind kind, byte[] bytes)
    {
        try
        {
            var result = kind switch
            {
                AttachmentKind.Docx => OfficeText.ReadDocx(bytes),
                AttachmentKind.Xlsx => OfficeText.ReadXlsx(bytes),
                _ => OfficeText.ReadPptx(bytes),
            };

            return result.Text.Length == 0
                ? new AttachmentContent(kind, null, null, "The document contains no readable text (it may consist only of images); the original was saved.", result.HiddenSkipped)
                : new AttachmentContent(kind, result.Text, null, null, result.HiddenSkipped);
        }
        catch (UserFacingException ex)
        {
            return new AttachmentContent(kind, null, null, ex.Message + " The original was saved.");
        }
    }

    /// <summary>The media folder inside an OOXML package, for embedded pictures.</summary>
    public static string? MediaFolder(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Docx => "word/media/",
        AttachmentKind.Xlsx => "xl/media/",
        AttachmentKind.Pptx => "ppt/media/",
        _ => null,
    };
}
