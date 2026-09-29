using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExtHost.Services;

/// <summary>讀取 Chrome 格式的 Bookmarks JSON 檔（Chrome、Edge 與 ExtHost 自己的 Bookmarks.json 都是這個格式）。</summary>
public sealed class ChromeBookmarkFile
{
    public BookmarkNode? BookmarkBar { get; private set; }
    public BookmarkNode? Other { get; private set; }
    public BookmarkNode? Synced { get; private set; }

    public static ChromeBookmarkFile Read(string path)
    {
        // Chrome 執行中也能讀（Chrome 不會獨佔這個檔案）
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(fs, JsonOptions);
        return FromDocument(doc);
    }

    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static ChromeBookmarkFile FromDocument(JsonDocument doc)
    {
        var result = new ChromeBookmarkFile();
        if (doc.RootElement.TryGetProperty("roots", out var roots) && roots.ValueKind == JsonValueKind.Object)
        {
            result.BookmarkBar = ParseRoot(roots, "bookmark_bar");
            result.Other = ParseRoot(roots, "other");
            result.Synced = ParseRoot(roots, "synced");
        }
        else
        {
            throw new InvalidDataException("不是 Chrome 的書籤檔（找不到 roots）");
        }
        return result;
    }

    private static BookmarkNode? ParseRoot(JsonElement roots, string name) =>
        roots.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? ParseNode(e) : null;

    private static BookmarkNode? ParseNode(JsonElement e)
    {
        var type = GetString(e, "type");
        var node = new BookmarkNode
        {
            Name = GetString(e, "name") ?? "",
            IsFolder = type == "folder" || (type == null && e.TryGetProperty("children", out _)),
        };
        if (long.TryParse(GetString(e, "id"), out var id))
        {
            node.Id = id;
        }
        if (long.TryParse(GetString(e, "date_added"), out var added))
        {
            node.DateAdded = added;
        }

        if (node.IsFolder)
        {
            if (e.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in children.EnumerateArray())
                {
                    var child = c.ValueKind == JsonValueKind.Object ? ParseNode(c) : null;
                    if (child != null)
                    {
                        child.Parent = node;
                        node.Children.Add(child);
                    }
                }
            }
        }
        else
        {
            node.Url = GetString(e, "url");
            if (string.IsNullOrEmpty(node.Url))
            {
                return null;
            }
        }
        return node;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;
}

/// <summary>偵測到的一個瀏覽器設定檔。</summary>
public sealed class BrowserProfile
{
    public required string Browser { get; init; }

    /// <summary>使用者在瀏覽器裡取的設定檔名稱，例如「個人」、「工作」。</summary>
    public required string ProfileName { get; init; }

    /// <summary>設定檔資料夾，例如 ...\User Data\Default。</summary>
    public required string ProfileDir { get; init; }

    public bool IsOnlyProfile { get; set; }

    public string DisplayName => IsOnlyProfile ? Browser : $"{Browser}（{ProfileName}）";
}

/// <summary>要匯入的書籤（尚未加入 BookmarkStore）。</summary>
public sealed class ImportedBookmarks
{
    /// <summary>來源瀏覽器名稱；HTML 檔案為空字串。</summary>
    public string SourceName { get; init; } = "";

    /// <summary>書籤列已有書籤時，匯入內容放進的資料夾名稱（與 Chrome 相同）。</summary>
    public string ImportFolderName => SourceName.Length > 0 ? $"從 {SourceName} 匯入" : "已匯入";

    public List<BookmarkNode> ToolbarItems { get; } = new();

    public List<BookmarkNode> OtherItems { get; } = new();

    public int UrlCount => All().Count(n => !n.IsFolder);

    public int FolderCount => All().Count(n => n.IsFolder);

    public bool IsEmpty => ToolbarItems.Count == 0 && OtherItems.Count == 0;

    private IEnumerable<BookmarkNode> All() =>
        ToolbarItems.Concat(OtherItems).SelectMany(n => new[] { n }.Concat(n.Descendants()));
}

public static class BookmarkImporter
{
    private static readonly (string Browser, string RelativeUserData)[] KnownBrowsers =
    {
        ("Google Chrome", @"Google\Chrome\User Data"),
        ("Google Chrome Beta", @"Google\Chrome Beta\User Data"),
        ("Google Chrome Dev", @"Google\Chrome Dev\User Data"),
        ("Google Chrome Canary", @"Google\Chrome SxS\User Data"),
        ("Microsoft Edge", @"Microsoft\Edge\User Data"),
        ("Microsoft Edge Beta", @"Microsoft\Edge Beta\User Data"),
        ("Microsoft Edge Dev", @"Microsoft\Edge Dev\User Data"),
        ("Chromium", @"Chromium\User Data"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data"),
    };

    /// <summary>Chrome 的使用者資料夾（用於檔案選擇對話框的起始位置）。</summary>
    public static string ChromeUserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data");

    // ======================= 偵測設定檔 =======================

    public static List<BrowserProfile> DetectProfiles()
    {
        var result = new List<BrowserProfile>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (browser, rel) in KnownBrowsers)
        {
            try
            {
                var userData = Path.Combine(local, rel);
                if (Directory.Exists(userData))
                {
                    var profiles = DetectProfilesIn(browser, userData);
                    if (profiles.Count == 1)
                    {
                        profiles[0].IsOnlyProfile = true;
                    }
                    result.AddRange(profiles);
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log($"偵測 {browser} 設定檔失敗：{ex.Message}");
            }
        }
        return result;
    }

    private static List<BrowserProfile> DetectProfilesIn(string browser, string userData)
    {
        // Local State 的 profile.info_cache 記錄每個設定檔資料夾對應的顯示名稱
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        string? lastUsed = null;
        try
        {
            var localState = Path.Combine(userData, "Local State");
            if (File.Exists(localState))
            {
                using var fs = new FileStream(localState, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.TryGetProperty("profile", out var profile))
                {
                    if (profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in cache.EnumerateObject())
                        {
                            var name = p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("name", out var n)
                                ? n.GetString()
                                : null;
                            names[p.Name] = string.IsNullOrWhiteSpace(name) ? p.Name : name!;
                            order.Add(p.Name);
                        }
                    }
                    if (profile.TryGetProperty("last_used", out var lu) && lu.ValueKind == JsonValueKind.String)
                    {
                        lastUsed = lu.GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"讀取 {browser} 的 Local State 失敗：{ex.Message}");
        }

        // 也掃描資料夾，避免 Local State 沒列到
        foreach (var dir in Directory.EnumerateDirectories(userData))
        {
            var name = Path.GetFileName(dir);
            if ((name == "Default" || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) && !order.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(name);
            }
        }

        var list = new List<BrowserProfile>();
        foreach (var dirName in order)
        {
            var dir = Path.Combine(userData, dirName);
            if (BookmarkFilesIn(dir).Count == 0)
            {
                continue;
            }
            list.Add(new BrowserProfile
            {
                Browser = browser,
                ProfileName = names.TryGetValue(dirName, out var n) ? n : dirName,
                ProfileDir = dir,
            });
        }

        // 最後使用的設定檔排最前面
        if (lastUsed != null)
        {
            var i = list.FindIndex(p => string.Equals(Path.GetFileName(p.ProfileDir), lastUsed, StringComparison.OrdinalIgnoreCase));
            if (i > 0)
            {
                var p = list[i];
                list.RemoveAt(i);
                list.Insert(0, p);
            }
        }
        return list;
    }

    /// <summary>設定檔資料夾裡的書籤檔：Bookmarks（本機）與 Account Bookmarks（新版 Chrome 登入帳號後的帳戶書籤）。</summary>
    public static List<string> BookmarkFilesIn(string profileDir)
    {
        var files = new List<string>();
        foreach (var name in new[] { "Bookmarks", "Account Bookmarks" })
        {
            var f = Path.Combine(profileDir, name);
            if (File.Exists(f))
            {
                files.Add(f);
            }
        }
        return files;
    }

    /// <summary>
    /// 把使用者輸入的路徑轉成書籤檔清單。
    /// 可以是 Bookmarks 檔本身，或 chrome://version「設定檔路徑」那個資料夾。
    /// </summary>
    public static List<string> ResolveManualPath(string input)
    {
        var path = input.Trim().Trim('"');
        if (path.Length == 0)
        {
            throw new InvalidOperationException("請輸入設定檔路徑或 Bookmarks 檔案位置。");
        }
        path = Environment.ExpandEnvironmentVariables(path);
        if (File.Exists(path))
        {
            return new List<string> { path };
        }
        if (Directory.Exists(path))
        {
            var files = BookmarkFilesIn(path);
            if (files.Count == 0)
            {
                // 可能選到 User Data 本身
                files = BookmarkFilesIn(Path.Combine(path, "Default"));
            }
            if (files.Count == 0)
            {
                throw new InvalidOperationException("這個資料夾裡找不到 Bookmarks 檔案：\n" + path
                    + "\n\n請確認是 chrome://version 裡「設定檔路徑」那一行的資料夾。");
            }
            return files;
        }
        throw new InvalidOperationException("找不到這個路徑：\n" + path);
    }

    // ======================= 讀取來源 =======================

    public static ImportedBookmarks ReadChromeFiles(IEnumerable<string> files, string sourceName)
    {
        var result = new ImportedBookmarks { SourceName = sourceName };
        foreach (var f in files)
        {
            var file = ChromeBookmarkFile.Read(f);
            if (file.BookmarkBar != null)
            {
                result.ToolbarItems.AddRange(Detach(file.BookmarkBar));
            }
            if (file.Other != null)
            {
                result.OtherItems.AddRange(Detach(file.Other));
            }
            if (file.Synced is { Children.Count: > 0 } synced)
            {
                var folder = BookmarkNode.NewFolder("行動裝置書籤");
                folder.DateAdded = synced.DateAdded != 0 ? synced.DateAdded : folder.DateAdded;
                foreach (var c in Detach(synced))
                {
                    c.Parent = folder;
                    folder.Children.Add(c);
                }
                result.OtherItems.Add(folder);
            }
        }
        return result;
    }

    private static List<BookmarkNode> Detach(BookmarkNode root)
    {
        var list = root.Children.ToList();
        root.Children.Clear();
        foreach (var c in list)
        {
            c.Parent = null;
        }
        return list;
    }

    private static readonly Regex TagRegex = new(@"<(/?)([A-Za-z0-9]+)([^>]*)>", RegexOptions.Compiled);
    private static readonly Regex AttrRegex = new(@"([A-Za-z_\-]+)\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Compiled);

    /// <summary>讀取 Netscape 格式的書籤 HTML 檔（Chrome、Edge、Firefox「匯出書籤」產生的檔案）。</summary>
    public static ImportedBookmarks ReadHtmlFile(string path)
    {
        var html = File.ReadAllText(path, Encoding.UTF8);
        var result = new ImportedBookmarks();

        // 虛擬根目錄；遇到 PERSONAL_TOOLBAR_FOLDER 的資料夾就把它的內容當成書籤列
        var root = BookmarkNode.NewFolder("");
        BookmarkNode? toolbar = null;
        var stack = new Stack<BookmarkNode>();
        stack.Push(root);
        BookmarkNode? pendingFolder = null;

        var matches = TagRegex.Matches(html);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var closing = m.Groups[1].Value == "/";
            var tag = m.Groups[2].Value.ToUpperInvariant();
            var attrs = m.Groups[3].Value;

            if (!closing && (tag == "H3" || tag == "A"))
            {
                // 讀到對應的結束標籤為止的文字
                var textStart = m.Index + m.Length;
                var end = html.IndexOf("</" + m.Groups[2].Value, textStart, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    continue;
                }
                var text = WebUtility.HtmlDecode(Regex.Replace(html[textStart..end], "<[^>]*>", "")).Trim();
                var attr = ParseAttributes(attrs);
                var added = attr.TryGetValue("ADD_DATE", out var ad) && long.TryParse(ad, out var secs) && secs > 0
                    ? BookmarkStore.ToChromeTime(DateTimeOffset.FromUnixTimeSeconds(Math.Min(secs, 253402300799)).UtcDateTime)
                    : 0;

                if (tag == "H3")
                {
                    pendingFolder = BookmarkNode.NewFolder(text);
                    if (added != 0)
                    {
                        pendingFolder.DateAdded = added;
                    }
                    Append(stack.Peek(), pendingFolder);
                    if (attr.TryGetValue("PERSONAL_TOOLBAR_FOLDER", out var tb) && tb.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        toolbar ??= pendingFolder;
                    }
                }
                else if (attr.TryGetValue("HREF", out var href) && href.Length > 0 && !href.StartsWith("place:", StringComparison.OrdinalIgnoreCase))
                {
                    var node = BookmarkNode.NewUrl(text, WebUtility.HtmlDecode(href));
                    if (added != 0)
                    {
                        node.DateAdded = added;
                    }
                    Append(stack.Peek(), node);
                }

                // 跳過已讀取的文字內的標籤
                while (i + 1 < matches.Count && matches[i + 1].Index < end)
                {
                    i++;
                }
            }
            else if (!closing && tag == "DL")
            {
                if (pendingFolder != null)
                {
                    stack.Push(pendingFolder);
                    pendingFolder = null;
                }
                else if (stack.Count > 1 || root.Children.Count > 0)
                {
                    // 沒有標題的 DL（不標準），當成目前資料夾的延續
                    stack.Push(stack.Peek());
                }
            }
            else if (closing && tag == "DL")
            {
                if (stack.Count > 1)
                {
                    stack.Pop();
                }
                pendingFolder = null;
            }
        }

        if (toolbar != null)
        {
            toolbar.Parent?.Children.Remove(toolbar);
            result.ToolbarItems.AddRange(Detach(toolbar));
        }
        result.OtherItems.AddRange(Detach(root));
        return result;
    }

    private static void Append(BookmarkNode parent, BookmarkNode node)
    {
        node.Parent = parent;
        parent.Children.Add(node);
    }

    private static Dictionary<string, string> ParseAttributes(string attrs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in AttrRegex.Matches(attrs))
        {
            var value = a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Success ? a.Groups[4].Value : a.Groups[5].Value;
            dict[a.Groups[1].Value] = value;
        }
        return dict;
    }

    // ======================= 寫入 =======================

    /// <summary>
    /// 依 Chrome 的規則匯入：
    /// 書籤列還是空的 → 書籤列項目直接放進書籤列、其他項目放進「其他書籤」；
    /// 書籤列已經有東西 → 全部放進書籤列上新建的「從 XXX 匯入」資料夾。
    /// </summary>
    public static void ImportInto(BookmarkStore store, ImportedBookmarks data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (store.BookmarkBar.Children.Count == 0)
        {
            store.AddTree(store.BookmarkBar, data.ToolbarItems);
            store.AddTree(store.Other, data.OtherItems);
        }
        else
        {
            var folder = BookmarkNode.NewFolder(data.ImportFolderName);
            foreach (var n in data.ToolbarItems.Concat(data.OtherItems))
            {
                n.Parent = folder;
                folder.Children.Add(n);
            }
            store.AddTree(store.BookmarkBar, new[] { folder });
        }
        store.Commit();
    }
}
