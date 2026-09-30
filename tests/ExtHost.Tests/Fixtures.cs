namespace ExtHost.Tests;

/// <summary>測試用的 Chrome 書籤檔與 HTML 書籤檔。</summary>
internal static class Fixtures
{
    public const string ChromeBookmarks = """
        {
           "checksum": "abc",
           "roots": {
              "bookmark_bar": {
                 "children": [ {
                    "date_added": "13300000000000000", "guid": "x", "id": "5", "name": "Google", "type": "url", "url": "https://www.google.com/"
                 }, {
                    "children": [ { "id": "7", "name": "工作 A", "type": "url", "url": "https://a.example.com/" } ],
                    "id": "6", "name": "工作", "type": "folder"
                 } ],
                 "id": "1", "name": "書籤列", "type": "folder"
              },
              "other": { "children": [ { "id": "8", "name": "Other1", "type": "url", "url": "https://o.example.com/" } ], "id": "2", "name": "其他書籤", "type": "folder" },
              "synced": { "children": [ { "id": "9", "name": "Mobile", "type": "url", "url": "https://m.example.com/" } ], "id": "3", "name": "行動裝置書籤", "type": "folder" }
           },
           "sync_metadata": "xxx",
           "version": 1
        }
        """;

    public const string ExportedHtml = """
        <!DOCTYPE NETSCAPE-Bookmark-file-1>
        <META HTTP-EQUIV="Content-Type" CONTENT="text/html; charset=UTF-8">
        <TITLE>Bookmarks</TITLE>
        <H1>Bookmarks</H1>
        <DL><p>
            <DT><H3 ADD_DATE="1700000000" LAST_MODIFIED="0" PERSONAL_TOOLBAR_FOLDER="true">書籤列</H3>
            <DL><p>
                <DT><A HREF="https://www.google.com/" ADD_DATE="1700000001" ICON="data:image/png;base64,AAA">Google &amp; Co</A>
                <DT><H3 ADD_DATE="1700000002">資料夾</H3>
                <DL><p>
                    <DT><A HREF="https://x.example.com/?a=1&amp;b=2">X</A>
                </DL><p>
            </DL><p>
            <DT><A HREF="https://other.example.com/">Other</A>
        </DL><p>
        """;
}
