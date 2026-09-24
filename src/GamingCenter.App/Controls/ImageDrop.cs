using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GamingCenter.App.Controls;

/// <summary>
/// Turns whatever a drag source offers into an image file on disk:
/// a file from Explorer, a picture dragged from Chrome/Edge (virtual file, bitmap, data: URI or image link),
/// or a clipboard image. The result is a temporary file that the image store then resizes and copies.
/// </summary>
public static partial class ImageDrop
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".jfif"];

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) mi2oGamingCenter");
        return http;
    }

    /// <summary>What was found in the drop: a ready file, or a web address still to download.</summary>
    public sealed record Result(string? File, string? Url);

    public static bool CanAccept(IDataObject data)
    {
        try
        {
            if (ImageFile(data) is not null) return true;
            if (data.GetDataPresent("FileGroupDescriptorW") || data.GetDataPresent(DataFormats.Bitmap)) return true;
            return data.GetDataPresent(DataFormats.Html) || data.GetDataPresent("UniformResourceLocatorW")
                || data.GetDataPresent("UniformResourceLocator") || data.GetDataPresent(DataFormats.UnicodeText);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads the drop synchronously (drag data is only valid during the Drop event).</summary>
    public static Result? Read(IDataObject data)
    {
        // 1. A real file (Explorer, or browsers that provide one).
        if (ImageFile(data) is { } file) return new Result(file, null);

        // 2. Picture embedded as data: URI (common for thumbnails in Google Images).
        var url = ImageUrl(data);
        if (url is not null && url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) && SaveDataUri(url) is { } fromUri)
            return new Result(fromUri, null);

        // 3. Browser "virtual file" (Chrome/Edge drag a picture as FileGroupDescriptor + FileContents).
        if (VirtualFile(data) is { } virtualFile) return new Result(virtualFile, null);

        // 4. Raw bitmap.
        if (BitmapData(data) is { } bitmapFile) return new Result(bitmapFile, null);

        // 5. Only a link: download it after the drop.
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            return new Result(null, url);

        return null;
    }

    /// <summary>Downloads a picture link to a temporary file. Throws with a readable message on failure.</summary>
    public static async Task<string> DownloadAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The website refused the download ({(int)response.StatusCode}). Save the picture first, then drop the file.");
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !LooksLikeImage(bytes))
            throw new InvalidOperationException("That link is a web page, not a picture. Open the picture itself, then drag it again.");
        var ext = type switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".jpg",
        };
        return SaveBytes(bytes, ext);
    }

    private static string? ImageFile(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && File.Exists(f))
            : null;

    private static string? ImageUrl(IDataObject data)
    {
        try
        {
            // The HTML fragment has the actual <img src>, which beats the page link.
            if (data.GetDataPresent(DataFormats.Html) && data.GetData(DataFormats.Html) is string html)
            {
                var m = ImgSrc().Match(html);
                if (m.Success) return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            }
            foreach (var format in new[] { "UniformResourceLocatorW", "UniformResourceLocator" })
            {
                if (!data.GetDataPresent(format)) continue;
                var raw = data.GetData(format);
                var text = raw switch
                {
                    MemoryStream ms when format.EndsWith('W') => Encoding.Unicode.GetString(ms.ToArray()),
                    MemoryStream ms => Encoding.ASCII.GetString(ms.ToArray()),
                    string s => s,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text)) return text.TrimEnd('\0').Trim();
            }
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string t)
            {
                t = t.Trim();
                if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase) || t.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)) return t;
            }
        }
        catch (Exception) { /* some drag sources throw on formats they announce */ }
        return null;
    }

    private static string? SaveDataUri(string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0 || !uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var bytes = Convert.FromBase64String(uri[(comma + 1)..]);
            var ext = uri.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase) ? ".png"
                : uri.StartsWith("data:image/webp", StringComparison.OrdinalIgnoreCase) ? ".webp"
                : uri.StartsWith("data:image/gif", StringComparison.OrdinalIgnoreCase) ? ".gif" : ".jpg";
            return SaveBytes(bytes, ext);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? VirtualFile(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent("FileGroupDescriptorW") || !data.GetDataPresent("FileContents")) return null;
            var name = VirtualFileName(data);
            // WPF's GetData asks for lindex -1, which Chrome rejects; read item 0 through COM first.
            var bytes = ComFileContents(data) ?? (data.GetData("FileContents") as MemoryStream)?.ToArray();
            if (bytes is null) return null;
            if (bytes.Length == 0 || !LooksLikeImage(bytes)) return null;
            var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            return SaveBytes(bytes, ImageExtensions.Contains(ext) ? ext : ".jpg");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads FileContents for the first virtual file via the OLE data object (lindex = 0).</summary>
    private static byte[]? ComFileContents(IDataObject data)
    {
        if (data is not System.Runtime.InteropServices.ComTypes.IDataObject com) return null;
        var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = (short)DataFormats.GetDataFormat("FileContents").Id,
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = 0,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM | System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
        };
        System.Runtime.InteropServices.ComTypes.STGMEDIUM medium = default;
        try
        {
            com.GetData(ref format, out medium);
            if (medium.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM)
            {
                var stream = (System.Runtime.InteropServices.ComTypes.IStream)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(medium.unionmember);
                using var ms = new MemoryStream();
                var buffer = new byte[64 * 1024];
                var readPtr = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    while (true)
                    {
                        stream.Read(buffer, buffer.Length, readPtr);
                        int read = System.Runtime.InteropServices.Marshal.ReadInt32(readPtr);
                        if (read <= 0) break;
                        ms.Write(buffer, 0, read);
                        if (ms.Length > 40 * 1024 * 1024) return null;
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(readPtr);
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(stream);
                }
                return ms.ToArray();
            }
            if (medium.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL)
            {
                var ptr = GlobalLock(medium.unionmember);
                try
                {
                    int size = (int)GlobalSize(medium.unionmember);
                    var bytes = new byte[size];
                    System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, size);
                    return bytes;
                }
                finally { GlobalUnlock(medium.unionmember); }
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (medium.unionmember != IntPtr.Zero) ReleaseStgMedium(ref medium);
        }
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    /// <summary>File name from FILEGROUPDESCRIPTORW (count, then FILEDESCRIPTORW with the name at offset 72).</summary>
    private static string? VirtualFileName(IDataObject data)
    {
        if (data.GetData("FileGroupDescriptorW") is not MemoryStream ms) return null;
        var buffer = ms.ToArray();
        const int nameOffset = 4 + 72;
        if (buffer.Length < nameOffset + 2) return null;
        var name = Encoding.Unicode.GetString(buffer, nameOffset, Math.Min(520, buffer.Length - nameOffset));
        var end = name.IndexOf('\0');
        return end >= 0 ? name[..end] : name;
    }

    private static string? BitmapData(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.Bitmap) || data.GetData(DataFormats.Bitmap) is not BitmapSource bmp) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return SaveBytes(ms.ToArray(), ".png");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool LooksLikeImage(byte[] b) =>
        b.Length > 12 && (
            (b[0] == 0xFF && b[1] == 0xD8) ||                                  // JPEG
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||  // PNG
            (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) ||                  // GIF
            (b[0] == 0x42 && b[1] == 0x4D) ||                                  // BMP
            (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45)); // WEBP

    private static string SaveBytes(byte[] bytes, string ext)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mi2o-drop");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [GeneratedRegex("<img[^>]+src\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();
}
