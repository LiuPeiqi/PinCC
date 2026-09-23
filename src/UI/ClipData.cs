using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using PinCC.Core;

namespace PinCC.UI;

/// <summary>Clipboard / drag-drop payloads → card content. Images are stored as PNG in the data folder.</summary>
public static partial class ClipData
{
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

    public static bool IsImageFile(string path) => ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Clipboard.GetDataObject with retries (another app may briefly hold the clipboard).</summary>
    public static IDataObject? Current()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.GetDataObject(); }
            catch (COMException) { Thread.Sleep(60); }
        }
        return null;
    }

    /// <summary>
    /// Markdown for pasting into an open editor: images → ![](img/…), files → links; null means "let the TextBox
    /// paste the text itself" (when <paramref name="preferText"/> and text is present).
    /// </summary>
    public static string? ToMarkdown(IDataObject data, Store store, bool preferText)
    {
        if (Files(data) is { Length: > 0 } files)
            return string.Join("\n", files.Select(f => IsImageFile(f) && StoreFile(f, store) is { } img ? $"![]({img})" : $"[{Path.GetFileName(f)}]({new Uri(f).AbsoluteUri})"));
        if (preferText && data.GetDataPresent(DataFormats.UnicodeText)) return null;
        return ImagePng(data) is { } png ? $"![]({store.SaveImage(png)})" : null;
    }

    public static string[]? Files(IDataObject data)
    {
        try { return data.GetDataPresent(DataFormats.FileDrop) ? data.GetData(DataFormats.FileDrop) as string[] : null; }
        catch (COMException) { return null; }
    }

    public static string? Text(IDataObject data)
    {
        try { return data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null; }
        catch (COMException) { return null; }
    }

    /// <summary>The page a browser copied from (CF_HTML "SourceURL:" header), if any.</summary>
    public static string? SourceUrl(IDataObject data)
    {
        try
        {
            if (data.GetData(DataFormats.Html) is string html && SourceRx().Match(html) is { Success: true } m) return m.Groups[1].Value.Trim();
        }
        catch (COMException) { }
        return null;
    }

    /// <summary>Copies an image file into the data folder; null when it cannot be read (or is over 50 MB).</summary>
    public static string? StoreFile(string path, Store store)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Shared read: the image may still be open in an editor or a screenshot tool that keeps its handle.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 50_000_000) return null;
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return store.SaveImage(bytes, ext is ".jpeg" ? ".jpg" : ext);
        }
        catch { return null; }
    }

    /// <summary>
    /// Image bytes as PNG: a "PNG" / "image/png" stream (keeps alpha), then DIB, then WPF's bitmap as a last resort.
    /// Any malformed clipboard payload yields null, never an exception into the UI.
    /// </summary>
    public static byte[]? ImagePng(IDataObject data)
    {
        foreach (var format in new[] { "PNG", "image/png" })
            if (Get(data, format) is MemoryStream { Length: > 8 } png && png.ToArray() is var bytes && Fetch.ImageExt(bytes) == ".png") return bytes;
        if (Get(data, DataFormats.Dib) is MemoryStream dib && DibToPng(dib.ToArray()) is { } fromDib) return fromDib;
        try { if (Get(data, DataFormats.Bitmap) is BitmapSource bmp) return Png(bmp); } catch { }
        return null;
    }

    static object? Get(IDataObject data, string format)
    {
        try { return data.GetDataPresent(format) ? data.GetData(format) : null; }
        catch { return null; } // COM / NotSupported / OutOfMemory from another app's clipboard data
    }

    /// <summary>CF_DIB is a BMP without its 14-byte file header: add one and decode. Null for anything malformed.</summary>
    static byte[]? DibToPng(byte[] dib)
    {
        try
        {
            if (dib.Length < 40) return null;
            int headerSize = BitConverter.ToInt32(dib, 0);
            int bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            if (headerSize is < 40 or > 124 || bitCount is < 1 or > 32 || (uint)clrUsed > 256) return null;
            int colors = clrUsed != 0 ? clrUsed : bitCount <= 8 ? 1 << bitCount : 0;
            int masks = compression == 3 && headerSize == 40 ? 12 : 0; // BI_BITFIELDS
            int offset = 14 + headerSize + masks + colors * 4;
            var file = new byte[14 + dib.Length];
            if (offset >= file.Length) return null;
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            BitConverter.GetBytes(file.Length).CopyTo(file, 2);
            BitConverter.GetBytes(offset).CopyTo(file, 10);
            dib.CopyTo(file, 14);
            var decoder = new BmpBitmapDecoder(new MemoryStream(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return Png(decoder.Frames[0]);
        }
        catch { return null; }
    }

    public static byte[] Png(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    [GeneratedRegex(@"^SourceURL:(.+)$", RegexOptions.Multiline)] private static partial Regex SourceRx();
}
