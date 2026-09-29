using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ExtHost.Services;

/// <summary>書籤無法寫入磁碟；這次的修改已經還原。</summary>
public sealed class BookmarkSaveException : Exception
{
    public BookmarkSaveException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

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

    private sealed record RemovedEntry(BookmarkNode Node, BookmarkNode Parent, int Index);

    /// <summary>刪除紀錄（每次刪除一組），供「復原」使用。</summary>
    private readonly Stack<List<RemovedEntry>> _undo = new();

    public bool CanUndo => _undo.Count > 0;

    public void Remove(BookmarkNode node) => RemoveMany(new[] { node });

    /// <summary>刪除多個項目（算一次動作，復原時一起還原）。已被一併刪除的子項目會略過。</summary>
    public void RemoveMany(IEnumerable<BookmarkNode> nodes)
    {
        var list = nodes.Where(n => !n.IsRoot && n.Parent != null).Distinct().ToList();
        list = list.Where(n => !list.Any(other => other != n && n.IsDescendantOf(other))).ToList();
        if (list.Count == 0)
        {
            return;
        }
        var group = new List<RemovedEntry>();
        foreach (var n in list)
        {
            var parent = n.Parent!;
            group.Add(new RemovedEntry(n, parent, parent.Children.IndexOf(n)));
            parent.Children.Remove(n);
            n.Parent = null;
        }
        _undo.Push(group);
        while (_undo.Count > 50)
        {
            // Stack 沒有移除最底層的方法，重建一次
            var keep = _undo.Take(50).Reverse().ToList();
            _undo.Clear();
            foreach (var g in keep)
            {
                _undo.Push(g);
            }
        }
        Commit();
    }

    /// <summary>復原最近一次刪除；回傳還原的項目。</summary>
    public IReadOnlyList<BookmarkNode> UndoRemove()
    {
        if (_undo.Count == 0)
        {
            return Array.Empty<BookmarkNode>();
        }
        var group = _undo.Pop();
        // 依刪除的相反順序放回原位置
        for (var i = group.Count - 1; i >= 0; i--)
        {
            var e = group[i];
            if (!IsAttached(e.Parent))
            {
                continue; // 原本的資料夾也已經被刪除
            }
            e.Node.Parent = e.Parent;
            e.Parent.Children.Insert(Math.Clamp(e.Index, 0, e.Parent.Children.Count), e.Node);
        }
        Commit();
        return group.Select(e => e.Node).ToList();
    }

    /// <summary>移到指定資料夾的 index 位置（index &lt; 0 表示最後面）。</summary>
    public void Move(BookmarkNode node, BookmarkNode newParent, int index)
    {
        if (!CanMove(node, newParent))
        {
            return;
        }
        MoveCore(node, newParent, index);
        Commit();
    }

    /// <summary>
    /// 把多個項目依序移到指定位置（拖曳多個項目時使用），只存檔一次。
    /// index &lt; 0 表示最後面；index 指的是移動前 newParent 裡的位置。
    /// </summary>
    public void MoveMany(IReadOnlyList<BookmarkNode> nodes, BookmarkNode newParent, int index)
    {
        var list = nodes.Where(n => CanMove(n, newParent)).ToList();
        list = list.Where(n => !list.Any(other => other != n && n.IsDescendantOf(other))).ToList();
        if (list.Count == 0)
        {
            return;
        }
        // 先找出插入點後面的「錨點」，移動完再依錨點決定位置，避免 index 因移除而偏移
        BookmarkNode? anchor = null;
        if (index >= 0)
        {
            anchor = newParent.Children.Skip(index).FirstOrDefault(c => !list.Contains(c));
        }
        foreach (var n in list)
        {
            n.Parent!.Children.Remove(n);
        }
        var at = anchor != null ? newParent.Children.IndexOf(anchor) : newParent.Children.Count;
        foreach (var n in list)
        {
            n.Parent = newParent;
            newParent.Children.Insert(at++, n);
        }
        Commit();
    }

    /// <summary>節點是否還在書籤樹裡（沒有被刪除）。</summary>
    public bool IsAttached(BookmarkNode node)
    {
        var n = node;
        while (n.Parent != null)
        {
            n = n.Parent;
        }
        return n == BookmarkBar || n == Other;
    }

    public static bool CanMove(BookmarkNode node, BookmarkNode newParent) =>
        !node.IsRoot && node.Parent != null && node != newParent && !newParent.IsDescendantOf(node) && newParent.IsFolder;

    private static void MoveCore(BookmarkNode node, BookmarkNode newParent, int index)
    {
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
    }

    /// <summary>資料夾內依名稱排序（資料夾在前）。</summary>
    public void SortByName(BookmarkNode folder)
    {
        var sorted = folder.Children
            .OrderBy(c => c.IsFolder ? 0 : 1)
            .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        folder.Children.Clear();
        folder.Children.AddRange(sorted);
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

    /// <summary>
    /// 儲存並通知變更（AddTree 之後要呼叫）。
    /// 存檔失敗時會把記憶體中的書籤還原成上次成功儲存的狀態，並擲出 <see cref="BookmarkSaveException"/>，
    /// 畫面不會顯示沒有保存的修改。
    /// </summary>
    public void Commit()
    {
        try
        {
            Save();
        }
        catch (Exception ex)
        {
            AppPaths.Log("儲存書籤失敗：" + ex);
            RestoreFromLastSaved();
            Changed?.Invoke(this, EventArgs.Empty);
            throw new BookmarkSaveException(
                LoadError != null
                    ? "書籤檔讀取失敗，為避免覆寫原本的書籤，目前無法修改書籤。這次的修改已取消。\n\n"
                      + "請修復或移除下列檔案後重新啟動 ExtHost：\n" + AppPaths.BookmarksFile
                    : "無法儲存書籤，這次的修改已取消。\n\n" + ex.Message + "\n\n書籤檔：" + AppPaths.BookmarksFile,
                ex);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>上次成功儲存（或載入）時的內容，存檔失敗時用來還原。</summary>
    private string _lastSavedJson = "";

    /// <summary>
    /// 啟動時讀取書籤檔失敗的原因；不為 null 時停止寫入書籤檔，避免用空白內容覆寫原本的書籤。
    /// </summary>
    public string? LoadError { get; private set; }

    private void RestoreFromLastSaved()
    {
        BookmarkBar.Children.Clear();
        Other.Children.Clear();
        _undo.Clear();
        try
        {
            var file = ChromeBookmarkFile.ReadJson(_lastSavedJson);
            LoadRoot(BookmarkBar, file.BookmarkBar);
            LoadRoot(Other, file.Other);
        }
        catch (Exception ex)
        {
            AppPaths.Log("還原書籤失敗：" + ex.Message);
        }
    }

    // ======================= 讀寫檔案 =======================

    public static BookmarkStore Load()
    {
        var store = new BookmarkStore();
        try
        {
            store.LoadFromDisk();
        }
        catch (Exception ex)
        {
            // 不要回傳一份會被存回去的空白書籤：記下錯誤、停止寫入，並保留一份備份
            AppPaths.Log("讀取書籤失敗：" + ex);
            store.BookmarkBar.Children.Clear();
            store.Other.Children.Clear();
            var backup = AppPaths.BookmarksFile + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backupNote;
            try
            {
                File.Copy(AppPaths.BookmarksFile, backup, false);
                backupNote = "已備份到：" + backup;
            }
            catch (Exception copyEx)
            {
                backupNote = "無法建立備份（" + copyEx.Message + "），請先自行複製這個檔案。";
            }
            store.LoadError = ex.Message + "\n\n書籤檔：" + AppPaths.BookmarksFile + "\n" + backupNote;
        }
        store._lastSavedJson = store.Serialize();
        return store;
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(AppPaths.BookmarksFile))
        {
            return;
        }
        var file = ChromeBookmarkFile.Read(AppPaths.BookmarksFile);
        LoadRoot(BookmarkBar, file.BookmarkBar);
        LoadRoot(Other, file.Other);
        var maxId = AllNodes().Select(n => n.Id).DefaultIfEmpty(2).Max();
        _nextId = Math.Max(3, maxId + 1);

        // 修正重複或無效的 id
        var seen = new HashSet<long> { 1, 2 };
        foreach (var n in AllNodes())
        {
            if (n.Id <= 0 || !seen.Add(n.Id))
            {
                n.Id = _nextId++;
                seen.Add(n.Id);
            }
        }
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

    /// <summary>寫入書籤檔；失敗時擲出例外（由 <see cref="Commit"/> 處理）。</summary>
    private void Save()
    {
        if (LoadError != null)
        {
            throw new InvalidOperationException("書籤檔讀取失敗，停止寫入以保護原本的書籤。");
        }
        var json = Serialize();
        AppPaths.EnsureCreated();
        var tmp = AppPaths.BookmarksFile + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, AppPaths.BookmarksFile, true);
        _lastSavedJson = json;
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
