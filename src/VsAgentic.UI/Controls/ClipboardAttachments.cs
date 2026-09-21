using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Turns whatever the clipboard is holding into attachments the CLI accepts.
/// </summary>
public static class ClipboardAttachments
{
    /// <summary>
    /// Anthropic resizes anything larger than this before the model sees it, and
    /// bills for the full upload either way, so shrink first. Screenshots from a
    /// 4K monitor are several megabytes; base64 adds another third on top.
    /// </summary>
    private const int MaxEdge = 1568;

    /// <summary>
    /// How many images one paste sends inline. Every one of them is decoded at
    /// full size before it is shrunk, and that happens on the UI thread, so a
    /// folder of photos copied in File Explorer would otherwise lock up Visual
    /// Studio and then put the whole set into one request as base64. Images past
    /// this many are attached as paths instead: nothing is decoded, nothing is
    /// dropped, and the CLI reads them off disk if it needs them.
    /// </summary>
    private const int MaxInlineImages = 5;

    /// <summary>
    /// Reads everything on the clipboard that can travel with a message, in the
    /// order it was copied. Images come back decoded, so they can go inline;
    /// everything else comes back as a path for the CLI to open itself. Empty
    /// when there is nothing to attach.
    ///
    /// Handles a bitmap (a screenshot, or a copy out of an image editor) as well
    /// as anything copied in File Explorer, one file or a whole selection.
    /// </summary>
    public static IReadOnlyList<IChatAttachment> TryRead()
    {
        try
        {
            // Files come first and win over text: copying them in Explorer is
            // deliberate, and the text some apps publish next to them is the
            // path we are attaching anyway.
            if (Clipboard.ContainsFileDropList())
            {
                var attachments = new List<IChatAttachment>();
                var inlined = 0;
                foreach (var path in Clipboard.GetFileDropList())
                {
                    var attachment = TryReadPath(path, inlined < MaxInlineImages);
                    if (attachment is null) continue;
                    if (attachment is ChatImageAttachment) inlined++;
                    attachments.Add(attachment);
                }
                if (attachments.Count > 0) return attachments;
            }

            // A bitmap loses to text. Excel, Word and some browsers publish a
            // picture of the selection next to the text they copy, and taking
            // the picture would paste an image of the cells the user meant to
            // paste as text. A screenshot carries no text format, so it still
            // attaches.
            if (!Clipboard.ContainsText() && Clipboard.ContainsImage())
            {
                var source = Clipboard.GetImage();
                if (source is not null)
                    return new IChatAttachment[] { FromBitmapSource(source) };
            }
        }
        catch
        {
            // The clipboard is shared with every other process on the machine and
            // can fail for reasons that have nothing to do with us. Pasting
            // nothing is the right answer.
        }

        return Array.Empty<IChatAttachment>();
    }

    /// <param name="mayInline">
    /// False once the paste has inlined as many images as it is allowed to. An
    /// image file is then attached as a path without being decoded, which is
    /// what keeps a large selection off the UI thread.
    /// </param>
    private static IChatAttachment? TryReadPath(string? path, bool mayInline)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Directory.Exists(path)) return new ChatFileAttachment(path!);
        if (!File.Exists(path)) return null;
        if (!mayInline) return new ChatFileAttachment(path!);

        try
        {
            var image = TryReadImageFile(path!);
            if (image is not null) return image;
        }
        catch
        {
            // A file that claims to be a PNG but will not decode is still worth
            // attaching — the CLI can go and look at it on disk.
        }

        return new ChatFileAttachment(path!);
    }

    /// <summary>
    /// Decodes a copied image file, or returns null when the extension is not one
    /// the API takes inline — those go out as paths instead.
    /// </summary>
    private static ChatImageAttachment? TryReadImageFile(string path)
    {
        var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };
        if (mediaType is null) return null;

        // Re-encode through the decoder so oversized files get shrunk too. GIF is
        // passed through untouched: decoding keeps only the first frame, which
        // would silently drop the animation.
        if (mediaType == "image/gif")
            return new ChatImageAttachment(File.ReadAllBytes(path), mediaType, path);

        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.UriSource = new Uri(path);
        decoded.EndInit();
        return FromBitmapSource(decoded, path);
    }

    /// <param name="sourcePath">
    /// Null for a bitmap off the clipboard, which came from no file the message
    /// could name.
    /// </param>
    private static ChatImageAttachment FromBitmapSource(BitmapSource source, string? sourcePath = null)
    {
        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource final = scale < 1.0
            ? new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale))
            : source;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new ChatImageAttachment(stream.ToArray(), "image/png", sourcePath);
    }
}
