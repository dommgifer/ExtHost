using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExtHost.Services;

/// <summary>
/// 網站圖示快取：分頁載入網頁時取得的 favicon 依網站（主機名稱）存到 Favicons 資料夾，
/// 書籤列用它來顯示圖示。沒開過的網站就沒有圖示，顯示預設圖示。
/// </summary>
public static class FaviconCache
{
    private static readonly Dictionary<string, ImageSource?> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>有網站的圖示新增或更新。</summary>
    public static event EventHandler? Changed;

    private static string? KeyFor(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }
        return uri.Host.ToLowerInvariant();
    }

    private static string FileFor(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(AppPaths.FaviconsDir, hash + ".png");
    }

    public static ImageSource? Get(string? url)
    {
        var key = KeyFor(url);
        if (key == null)
        {
            return null;
        }
        if (Memory.TryGetValue(key, out var cached))
        {
            return cached;
        }
        ImageSource? img = null;
        try
        {
            var file = FileFor(key);
            if (File.Exists(file))
            {
                img = Decode(File.ReadAllBytes(file));
            }
        }
        catch
        {
            img = null;
        }
        Memory[key] = img;
        return img;
    }

    /// <summary>儲存網頁的 favicon（PNG）。</summary>
    public static void Store(string? pageUrl, byte[] png)
    {
        var key = KeyFor(pageUrl);
        if (key == null || png.Length == 0)
        {
            return;
        }
        var hash = Convert.ToHexString(SHA1.HashData(png));
        if (Hashes.TryGetValue(key, out var old) && old == hash)
        {
            return;
        }
        Hashes[key] = hash;

        try
        {
            var file = FileFor(key);
            if (File.Exists(file))
            {
                var existing = File.ReadAllBytes(file);
                if (Convert.ToHexString(SHA1.HashData(existing)) == hash)
                {
                    return;
                }
            }
            var img = Decode(png);
            if (img == null)
            {
                return;
            }
            Directory.CreateDirectory(AppPaths.FaviconsDir);
            File.WriteAllBytes(file, png);
            Memory[key] = img;
            Changed?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppPaths.Log("儲存網站圖示失敗：" + ex.Message);
        }
    }

    private static ImageSource? Decode(byte[] data)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
