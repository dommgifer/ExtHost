using ExtHost.Services;
using Xunit;

namespace ExtHost.Tests;

/// <summary>讀檔 / 存檔失敗時不能遺失或覆寫書籤（PR #1 審查意見）。</summary>
public class SaveFailureTests : StoreTestBase
{
    [Fact]
    public void BrokenFile_IsBackedUpAndNeverOverwritten()
    {
        const string broken = "{ \"roots\": { broken";
        File.WriteAllText(AppPaths.BookmarksFile, broken);

        var store = BookmarkStore.Load();

        Assert.NotNull(store.LoadError);
        Assert.Single(Directory.GetFiles(AppPaths.Root, "Bookmarks.json.broken-*"));
        Assert.Throws<BookmarkSaveException>(() => store.AddUrl(store.BookmarkBar, -1, "x", "https://x/"));
        Assert.Empty(store.BookmarkBar.Children);
        Assert.Equal(broken, File.ReadAllText(AppPaths.BookmarksFile));
    }

    [Fact]
    public void SaveFailure_RollsBackAndNotifies()
    {
        var store = BookmarkStore.Load();
        store.AddUrl(store.BookmarkBar, -1, "keep", "https://keep/");
        var changed = 0;
        store.Changed += (_, _) => changed++;
        BreakSaving();

        Assert.Throws<BookmarkSaveException>(() => store.AddUrl(store.BookmarkBar, -1, "lost", "https://lost/"));
        Assert.Single(store.BookmarkBar.Children);
        Assert.Equal("https://keep/", store.BookmarkBar.Children[0].Url);
        Assert.Equal(1, changed);

        Assert.Throws<BookmarkSaveException>(() => store.Remove(store.BookmarkBar.Children[0]));
        Assert.Single(store.BookmarkBar.Children);
        Assert.False(store.CanUndo);

        RestoreSaving();
        store.AddUrl(store.BookmarkBar, -1, "after", "https://after/");
        Assert.Equal(2, BookmarkStore.Load().BookmarkBar.Children.Count);
    }

    [Fact]
    public void SaveFailure_KeepsNodeIdentityInSubfolder()
    {
        var store = BookmarkStore.Load();
        var sub = store.AddFolder(store.BookmarkBar, -1, "Sub"); // 書籤管理員停在這個資料夾
        var a = store.AddUrl(sub, -1, "a", "https://a/");
        BreakSaving();

        Assert.Throws<BookmarkSaveException>(() => store.AddUrl(sub, -1, "ghost", "https://ghost/"));
        Assert.DoesNotContain(store.AllNodes(), n => n.Url == "https://ghost/");
        Assert.True(store.IsAttached(sub));
        Assert.Same(store.BookmarkBar, sub.Parent);
        Assert.Same(a, Assert.Single(sub.Children));
        Assert.Same(sub, a.Parent);

        Assert.Throws<BookmarkSaveException>(() => store.Update(a, "renamed", "https://changed/"));
        Assert.Equal("a", a.Name);
        Assert.Equal("https://a/", a.Url);

        Assert.Throws<BookmarkSaveException>(() => store.Remove(sub));
        Assert.Same(store.BookmarkBar, sub.Parent);
        Assert.True(store.IsAttached(sub));

        Assert.Throws<BookmarkSaveException>(() => store.Move(a, store.Other, -1));
        Assert.Same(sub, a.Parent);
        Assert.DoesNotContain(a, store.Other.Children);

        var node = BookmarkNode.NewUrl("t", "https://t/");
        Assert.Throws<BookmarkSaveException>(() =>
        {
            store.AddTree(sub, new[] { node });
            store.Commit();
        });
        Assert.False(store.IsAttached(node));
        Assert.DoesNotContain(node, sub.Children);

        // 恢復可寫入後，用同一個資料夾參照繼續操作，要能存到磁碟
        RestoreSaving();
        store.AddUrl(sub, -1, "b", "https://b/");
        store.Update(a, "a2", null);
        var reloaded = BookmarkStore.Load();
        var rsub = reloaded.BookmarkBar.Children.Single(c => c.Name == "Sub");
        Assert.Equal("a2b", Names(rsub));
        Assert.DoesNotContain(reloaded.AllNodes(), n => n.Url is "https://ghost/" or "https://t/");
    }
}
