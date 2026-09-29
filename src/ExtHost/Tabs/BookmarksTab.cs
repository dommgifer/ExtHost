using System.Windows;
using ExtHost.Bookmarks;
using ExtHost.Services;

namespace ExtHost.Tabs;

/// <summary>書籤管理員分頁（WPF 原生畫面，不是網頁）。</summary>
public sealed class BookmarksTab : TabBase
{
    private readonly BookmarkManagerPage _page;

    public BookmarksTab(IBookmarkHost host)
    {
        _page = new BookmarkManagerPage(host);
        Title = "書籤";
        Url = UrlHelper.BookmarksPageUrl;
    }

    public override FrameworkElement View => _page;

    public BookmarkManagerPage Page => _page;

    public override string Glyph => BookmarkUi.StarGlyph;

    public override void OnActivated()
    {
        _page.Refresh();
        _page.FocusList();
    }

    public override void Close() => _page.Detach();
}
