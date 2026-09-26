using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ExtHost.Services;

/// <summary>一個書籤或資料夾。</summary>
public sealed class BookmarkNode
{
    public long Id { get; set; }

    public bool IsFolder { get; set; }

    public string Name { get; set; } = "";

    /// <summary>網址（資料夾為 null）。</summary>
    public string? Url { get; set; }

    /// <summary>新增時間，格式與 Chrome 相同：自 1601-01-01 UTC 起的微秒數。</summary>
    public long DateAdded { get; set; }

    public List<BookmarkNode> Children { get; } = new();

    public BookmarkNode? Parent { get; set; }

    public bool IsRoot => Parent == null;

    /// <summary>顯示用名稱：沒有名稱的書籤顯示網址。</summary>
    public string DisplayName => Name.Length > 0 || IsFolder ? Name : Url ?? "";

    public int IndexInParent => Parent?.Children.IndexOf(this) ?? -1;

    public bool IsDescendantOf(BookmarkNode folder)
    {
        for (var p = Parent; p != null; p = p.Parent)
        {
            if (p == folder)
            {
                return true;
            }
        }
        return false;
    }

    public IEnumerable<BookmarkNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants())
            {
                yield return d;
            }
        }
    }

    public static BookmarkNode NewUrl(string name, string url) =>
        new() { Name = name, Url = url, DateAdded = BookmarkStore.NowChromeTime() };

    public static BookmarkNode NewFolder(string name) =>
        new() { Name = name, IsFolder = true, DateAdded = BookmarkStore.NowChromeTime() };
}

/// <summary>
/// ExtHost 自己的書籤，存在 Bookmarks.json。
/// 檔案格式沿用 Chrome 的 Bookmarks JSON（roots.bookmark_bar / roots.other），方便日後匯出或比對。
/// </summary>
public sealed class BookmarkStore
{
    private long _nextId = 3;

    public BookmarkNode BookmarkBar { get; } = new() { Id = 1, IsFolder = true, Name = "書籤列" };

    public BookmarkNode Other { get; } = new() { Id = 2, IsFolder = true, Name = "其他書籤" };

    public IEnumerable<BookmarkNode> Roots
    {
        get
        {
            yield return BookmarkBar;
            yield return Other;
        }
    }

    /// <summary>書籤有任何變更（新增、刪除、修改、移動、匯入）。</summary>
    public event EventHandler? Changed;

    public static long NowChromeTime() => ToChromeTime(DateTime.UtcNow);

    public static long ToChromeTime(DateTime utc) => (utc - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10;

    // ======================= 查詢 =======================

    public IEnumerable<BookmarkNode> AllNodes() => Roots.SelectMany(r => r.Descendants());

    public BookmarkNode? FindById(long id) =>
        Roots.FirstOrDefault(r => r.Id == id) ?? AllNodes().FirstOrDefault(n => n.Id == id);

    /// <summary>找出指定網址的書籤（最近新增的優先），沒有則回傳 null。</summary>
    public BookmarkNode? FindByUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        return AllNodes()
            .Where(n => !n.IsFolder && string.Equals(n.Url, url, StringComparison.Ordinal))
            .OrderByDescending(n => n.DateAdded)
            .FirstOrDefault();
    }

    public IEnumerable<BookmarkNode> FindAllByUrl(string url) =>
        AllNodes().Where(n => !n.IsFolder && string.Equals(n.Url, url, StringComparison.Ordinal)).ToList();

    /// <summary>所有資料夾（含根目錄），依樹狀順序，附深度。</summary>
    public IEnumerable<(BookmarkNode Folder, int Depth)> AllFolders()
    {
        IEnumerable<(BookmarkNode, int)> Walk(BookmarkNode folder, int depth)
        {
            yield return (folder, depth);
            foreach (var c in folder.Children.Where(c => c.IsFolder))
            {
                foreach (var x in Walk(c, depth + 1))
                {
                    yield return x;
                }
            }
        }
        return Roots.SelectMany(r => Walk(r, 0));
    }

    // ======================= 修改 =======================

    public BookmarkNode AddUrl(BookmarkNode parent, int index, string name, string url) =>
        Insert(parent, index, BookmarkNode.NewUrl(name, url));

    public BookmarkNode AddFolder(BookmarkNode parent, int index, string name) =>
        Insert(parent, index, BookmarkNode.NewFolder(name));

    private BookmarkNode Insert(BookmarkNode parent, int index, BookmarkNode node)
    {
        if (!parent.IsFolder)
        {
            throw new ArgumentException("只能加到資料夾", nameof(parent));
        }
        node.Id = _nextId++;
        node.Parent = parent;
        parent.Children.Insert(Math.Clamp(index < 0 ? parent.Children.Count : index, 0, parent.Children.Count), node);
        Commit();
        return node;
    }

    public void Update(BookmarkNode node, string name, string? url)
    {
        node.Name = name;
        if (!node.IsFolder && url != null)
        {
            node.Url = url;
        }
        Commit();
    }

    public void Remove(BookmarkNode node)
    {
        if (node.IsRoot)
        {
            return;
        }
        node.Parent!.Children.Remove(node);
        node.Parent = null;
        Commit();
    }

    /// <summary>移到指定資料夾的 index 位置（index &lt; 0 表示最後面）。</summary>
    public void Move(BookmarkNode node, BookmarkNode newParent, int index)
    {
        if (node.IsRoot || node == newParent || newParent.IsDescendantOf(node) || !newParent.IsFolder)
        {
            return;
        }
        var old = node.Parent!;
        var oldIndex = old.Children.IndexOf(node);
        old.Children.RemoveAt(oldIndex);
        if (index < 0)
        {
            index = newParent.Children.Count;
        }
        else if (old == newParent && oldIndex < index)
        {
            index--;
        }
        node.Parent = newParent;
        newParent.Children.Insert(Math.Clamp(index, 0, newParent.Children.Count), node);
        Commit();
    }

    /// <summary>把一批（尚未加入的）節點整批加到資料夾，只存檔一次。</summary>
    public void AddTree(BookmarkNode parent, IEnumerable<BookmarkNode> nodes)
    {
        foreach (var n in nodes)
        {
            AttachTree(parent, n);
        }
    }

    private void AttachTree(BookmarkNode parent, BookmarkNode node)
    {
        node.Id = _nextId++;
        node.Parent = parent;
        if (node.DateAdded == 0)
        {
            node.DateAdded = NowChromeTime();
        }
        parent.Children.Add(node);
        var children = node.Children.ToList();
        node.Children.Clear();
        foreach (var c in children)
        {
            AttachTree(node, c);
        }
    }

    /// <summary>儲存並通知變更（AddTree 之後要呼叫）。</summary>
    public void Commit()
    {
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ======================= 讀寫檔案 =======================

    public static BookmarkStore Load()
    {
        var store = new BookmarkStore();
        try
        {
            if (File.Exists(AppPaths.BookmarksFile))
            {
                var file = ChromeBookmarkFile.Read(AppPaths.BookmarksFile);
                store.LoadRoot(store.BookmarkBar, file.BookmarkBar);
                store.LoadRoot(store.Other, file.Other);
                var maxId = store.AllNodes().Select(n => n.Id).DefaultIfEmpty(2).Max();
                store._nextId = Math.Max(3, maxId + 1);

                // 修正重複或無效的 id
                var seen = new HashSet<long> { 1, 2 };
                foreach (var n in store.AllNodes())
                {
                    if (n.Id <= 0 || !seen.Add(n.Id))
                    {
                        n.Id = store._nextId++;
                        seen.Add(n.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("讀取書籤失敗：" + ex.Message);
            try
            {
                File.Copy(AppPaths.BookmarksFile, AppPaths.BookmarksFile + ".broken", true);
            }
            catch
            {
            }
        }
        return store;
    }

    private void LoadRoot(BookmarkNode root, BookmarkNode? loaded)
    {
        if (loaded == null)
        {
            return;
        }
        if (loaded.DateAdded != 0)
        {
            root.DateAdded = loaded.DateAdded;
        }
        foreach (var c in loaded.Children)
        {
            c.Parent = root;
            root.Children.Add(c);
        }
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            var tmp = AppPaths.BookmarksFile + ".tmp";
            File.WriteAllText(tmp, Serialize(), new UTF8Encoding(false));
            File.Move(tmp, AppPaths.BookmarksFile, true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("儲存書籤失敗：" + ex.Message);
        }
    }

    private string Serialize()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteStartObject("roots");
            w.WritePropertyName("bookmark_bar");
            WriteNode(w, BookmarkBar);
            w.WritePropertyName("other");
            WriteNode(w, Other);
            w.WriteEndObject();
            w.WriteNumber("version", 1);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter w, BookmarkNode n)
    {
        w.WriteStartObject();
        if (n.IsFolder)
        {
            w.WriteStartArray("children");
            foreach (var c in n.Children)
            {
                WriteNode(w, c);
            }
            w.WriteEndArray();
        }
        w.WriteString("date_added", n.DateAdded.ToString());
        w.WriteString("id", n.Id.ToString());
        w.WriteString("name", n.Name);
        w.WriteString("type", n.IsFolder ? "folder" : "url");
        if (!n.IsFolder)
        {
            w.WriteString("url", n.Url ?? "");
        }
        w.WriteEndObject();
    }
}
