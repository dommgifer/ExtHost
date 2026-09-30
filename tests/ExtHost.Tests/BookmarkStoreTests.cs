using ExtHost.Services;
using Xunit;

namespace ExtHost.Tests;

public class BookmarkStoreTests : StoreTestBase
{
    private static BookmarkStore StoreWithImport()
    {
        var store = BookmarkStore.Load();
        var file = TempFile(Path.Combine("Default", "Bookmarks"), Fixtures.ChromeBookmarks);
        BookmarkImporter.ImportInto(store, BookmarkImporter.ReadChromeFiles(new[] { file }, "Google Chrome"));
        return store;
    }

    [Fact]
    public void SaveAndReload_RoundTrips()
    {
        var store = StoreWithImport();

        var reloaded = BookmarkStore.Load();

        Assert.Equal(Dump(store.BookmarkBar), Dump(reloaded.BookmarkBar));
        Assert.Equal(Dump(store.Other), Dump(reloaded.Other));
        // 自己的檔案也是 Chrome 格式
        Assert.Equal(2, ChromeBookmarkFile.Read(AppPaths.BookmarksFile).BookmarkBar!.Children.Count);
    }

    [Fact]
    public void AddMoveRemove()
    {
        var store = StoreWithImport();
        var n = store.AddUrl(store.BookmarkBar, 0, "New", "https://new.example.com/");
        Assert.Same(n, store.BookmarkBar.Children[0]);
        Assert.Single(store.AllNodes(), x => x.Id == n.Id);

        var folder = store.BookmarkBar.Children.First(c => c.IsFolder);
        store.Move(n, folder, -1);
        Assert.Same(n, folder.Children.Last());
        Assert.Same(folder, n.Parent);

        store.Remove(n);
        Assert.Null(store.FindByUrl("https://new.example.com/"));
    }

    [Fact]
    public void MoveFolderIntoItsOwnChild_IsIgnored()
    {
        var store = BookmarkStore.Load();
        var folder = store.AddFolder(store.BookmarkBar, -1, "F");
        var child = store.AddFolder(folder, -1, "C");

        store.Move(folder, child, 0);
        store.MoveMany(new[] { folder }, folder, 0);

        Assert.Same(store.BookmarkBar, folder.Parent);
    }

    [Fact]
    public void MoveWithinSameParent_AdjustsIndex()
    {
        var store = BookmarkStore.Load();
        var bar = store.BookmarkBar;
        var a = store.AddUrl(bar, -1, "a", "https://a/");
        store.AddUrl(bar, -1, "b", "https://b/");
        store.AddUrl(bar, -1, "c", "https://c/");

        store.Move(a, bar, 2);

        Assert.Equal("bac", Names(bar));
    }

    [Fact]
    public void MoveMany_KeepsOrderAndPosition()
    {
        var store = BookmarkStore.Load();
        var b = store.BookmarkBar;
        var x1 = store.AddUrl(b, -1, "1", "https://1/");
        var x2 = store.AddUrl(b, -1, "2", "https://2/");
        var x3 = store.AddUrl(b, -1, "3", "https://3/");
        var x4 = store.AddUrl(b, -1, "4", "https://4/");

        store.MoveMany(new[] { x1, x2 }, b, 3);
        Assert.Equal("3124", Names(b));

        store.MoveMany(new[] { x4 }, b, 0);
        Assert.Equal("4312", Names(b));

        store.MoveMany(new[] { x3, x1 }, b, -1);
        Assert.Equal("4231", Names(b));
    }

    [Fact]
    public void UndoRemove_RestoresOriginalPositions()
    {
        var store = StoreWithImport();
        var before = Dump(store.BookmarkBar);
        var folder = store.BookmarkBar.Children.First(c => c.IsFolder);

        store.RemoveMany(new[] { folder, folder.Children[0], store.BookmarkBar.Children.Last() });
        Assert.False(store.IsAttached(folder));
        Assert.True(store.CanUndo);

        store.UndoRemove();
        Assert.Equal(before, Dump(store.BookmarkBar));
        Assert.False(store.CanUndo);
    }

    [Fact]
    public void SortByName_PutsFoldersFirst()
    {
        var store = BookmarkStore.Load();
        var bar = store.BookmarkBar;
        store.AddUrl(bar, -1, "b", "https://b/");
        store.AddFolder(bar, -1, "z");
        store.AddUrl(bar, -1, "a", "https://a/");

        store.SortByName(bar);

        Assert.Equal("zab", Names(bar));
    }
}
