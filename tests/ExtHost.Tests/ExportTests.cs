using ExtHost.Services;
using Xunit;

namespace ExtHost.Tests;

public class ExportTests : StoreTestBase
{
    [Fact]
    public void ExportThenImport_RoundTrips()
    {
        var store = BookmarkStore.Load();
        var file = TempFile(Path.Combine("Default", "Bookmarks"), Fixtures.ChromeBookmarks);
        BookmarkImporter.ImportInto(store, BookmarkImporter.ReadChromeFiles(new[] { file }, "Google Chrome"));

        var html = TempFile("out.html", BookmarkExporter.ToHtml(store));
        var back = BookmarkImporter.ReadHtmlFile(html);

        Assert.Equal(DumpList(store.BookmarkBar.Children), DumpList(back.ToolbarItems));
        Assert.Equal(DumpList(store.Other.Children), DumpList(back.OtherItems));

        // 新增時間（HTML 只記到秒）
        var original = store.BookmarkBar.Children.First(c => !c.IsFolder);
        var exported = back.ToolbarItems.SelectMany(x => new[] { x }.Concat(x.Descendants())).First(x => x.Url == original.Url);
        Assert.InRange(exported.DateAdded / 1_000_000 - original.DateAdded / 1_000_000, -1, 1);
    }

    [Fact]
    public void DefaultFileName_MatchesChrome()
    {
        Assert.Equal("bookmarks_9_29_26.html", BookmarkExporter.DefaultFileName(new DateTime(2026, 9, 29)));
    }
}
