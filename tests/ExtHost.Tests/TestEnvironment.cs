using System.Runtime.CompilerServices;
using ExtHost.Services;
using Xunit;

// 所有測試共用同一個 Bookmarks.json，不能平行執行
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ExtHost.Tests;

internal static class TestEnvironment
{
    /// <summary>在任何測試存取 AppPaths 之前，把資料資料夾指到暫存目錄，避免動到真正的使用者資料。</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ExtHost.Tests-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("EXTHOST_DATA_DIR", dir);
    }
}

/// <summary>每個測試開始前清空書籤檔（包含測試故意建立的同名資料夾、備份與暫存檔）。</summary>
public abstract class StoreTestBase : IDisposable
{
    protected StoreTestBase()
    {
        Clean();
    }

    public void Dispose() => Clean();

    private static void Clean()
    {
        AppPaths.EnsureCreated();
        var file = AppPaths.BookmarksFile;
        if (Directory.Exists(file))
        {
            Directory.Delete(file, true);
        }
        foreach (var f in Directory.GetFiles(AppPaths.Root, "Bookmarks.json*"))
        {
            File.Delete(f);
        }
    }

    /// <summary>讓存檔失敗：在書籤檔的位置放一個同名資料夾。</summary>
    protected static void BreakSaving()
    {
        if (File.Exists(AppPaths.BookmarksFile))
        {
            File.Delete(AppPaths.BookmarksFile);
        }
        Directory.CreateDirectory(AppPaths.BookmarksFile);
    }

    protected static void RestoreSaving()
    {
        Directory.Delete(AppPaths.BookmarksFile, true);
        var tmp = AppPaths.BookmarksFile + ".tmp";
        if (File.Exists(tmp))
        {
            File.Delete(tmp);
        }
    }

    protected static string Dump(BookmarkNode n, int depth = 0) =>
        new string(' ', depth * 2) + (n.IsFolder ? "[F] " : "") + n.Name + (n.Url != null ? " <" + n.Url + ">" : "") + "\n"
        + string.Concat(n.Children.Select(c => Dump(c, depth + 1)));

    protected static string DumpList(IEnumerable<BookmarkNode> nodes) => string.Concat(nodes.Select(n => Dump(n)));

    protected static string Names(BookmarkNode folder) => string.Concat(folder.Children.Select(c => c.Name));

    /// <summary>建立暫存檔案，回傳路徑。</summary>
    protected static string TempFile(string name, string content)
    {
        var dir = Path.Combine(AppPaths.Root, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
