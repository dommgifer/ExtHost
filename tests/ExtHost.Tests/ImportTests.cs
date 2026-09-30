using ExtHost.Services;
using Xunit;

namespace ExtHost.Tests;

public class ImportTests : StoreTestBase
{
    private static ImportedBookmarks ReadChromeProfile()
    {
        var file = TempFile(Path.Combine("Default", "Bookmarks"), Fixtures.ChromeBookmarks);
        // 模擬使用者貼上 chrome://version 的「設定檔路徑」
        var files = BookmarkImporter.ResolveManualPath(Path.GetDirectoryName(file)!);
        return BookmarkImporter.ReadChromeFiles(files, "Google Chrome");
    }

    [Fact]
    public void ReadsChromeProfile()
    {
        var data = ReadChromeProfile();
        Assert.Equal(4, data.UrlCount);
        Assert.Equal(2, data.FolderCount);
    }

    [Fact]
    public void ImportIntoEmptyBar_GoesToTopLevel()
    {
        var store = BookmarkStore.Load();
        Assert.Empty(store.BookmarkBar.Children);

        BookmarkImporter.ImportInto(store, ReadChromeProfile());

        Assert.Equal(2, store.BookmarkBar.Children.Count);
        Assert.Equal("Google", store.BookmarkBar.Children[0].Name);
        Assert.Equal(2, store.Other.Children.Count);
        Assert.Equal("行動裝置書籤", store.Other.Children[1].Name);
        Assert.Equal("工作", store.FindByUrl("https://a.example.com/")?.Parent?.Name);

        var ids = store.AllNodes().Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 2));
    }

    [Fact]
    public void ImportIntoNonEmptyBar_GoesIntoFolder()
    {
        var store = BookmarkStore.Load();
        BookmarkImporter.ImportInto(store, ReadChromeProfile());

        BookmarkImporter.ImportInto(store, BookmarkImporter.ReadHtmlFile(TempFile("bookmarks.html", Fixtures.ExportedHtml)));

        var last = store.BookmarkBar.Children.Last();
        Assert.True(last.IsFolder);
        Assert.Equal("已匯入", last.Name);
        Assert.Equal(3, last.Children.Count);
    }

    [Fact]
    public void BrowserImportFolderIsNamedAfterBrowser()
    {
        var store = BookmarkStore.Load();
        store.AddUrl(store.BookmarkBar, -1, "existing", "https://existing/");

        BookmarkImporter.ImportInto(store, ReadChromeProfile());

        Assert.Equal("從 Google Chrome 匯入", store.BookmarkBar.Children.Last().Name);
    }

    [Fact]
    public void ReadsHtmlAndDecodesEntities()
    {
        var html = BookmarkImporter.ReadHtmlFile(TempFile("bookmarks.html", Fixtures.ExportedHtml));

        Assert.Equal(2, html.ToolbarItems.Count);
        Assert.Single(html.OtherItems);
        Assert.Equal("Google & Co", html.ToolbarItems[0].Name);
        Assert.Equal("https://x.example.com/?a=1&b=2", html.ToolbarItems[1].Children[0].Url);
    }

    [Fact]
    public void ManualPath_MissingBookmarksFile_Throws()
    {
        var dir = Path.GetDirectoryName(TempFile("placeholder.txt", ""))!;
        Assert.Throws<InvalidOperationException>(() => BookmarkImporter.ResolveManualPath(dir));
    }
}
