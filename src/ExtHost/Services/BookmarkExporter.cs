using System.IO;
using System.Net;
using System.Text;

namespace ExtHost.Services;

/// <summary>匯出成 Netscape 書籤 HTML 檔（與 Chrome「匯出書籤」相同格式，Chrome / Edge / Firefox 都能匯入）。</summary>
public static class BookmarkExporter
{
    /// <summary>Chrome 的預設檔名：bookmarks_月_日_年.html</summary>
    public static string DefaultFileName(DateTime now) => $"bookmarks_{now.Month}_{now.Day}_{now:yy}.html";

    public static void ExportHtml(BookmarkStore store, string path)
    {
        File.WriteAllText(path, ToHtml(store), new UTF8Encoding(false));
    }

    public static string ToHtml(BookmarkStore store)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n");
        sb.Append("<!-- This is an automatically generated file.\n     It will be read and overwritten.\n     DO NOT EDIT! -->\n");
        sb.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n");
        sb.Append("<TITLE>Bookmarks</TITLE>\n");
        sb.Append("<H1>Bookmarks</H1>\n");
        sb.Append("<DL><p>\n");

        // 書籤列是帶 PERSONAL_TOOLBAR_FOLDER 的資料夾，其他書籤直接列在最外層（與 Chrome 相同）
        var bar = store.BookmarkBar;
        sb.Append("    <DT><H3 ADD_DATE=\"").Append(UnixSeconds(bar.DateAdded))
          .Append("\" LAST_MODIFIED=\"0\" PERSONAL_TOOLBAR_FOLDER=\"true\">").Append(Encode(bar.Name)).Append("</H3>\n");
        sb.Append("    <DL><p>\n");
        foreach (var c in bar.Children)
        {
            WriteNode(sb, c, 2);
        }
        sb.Append("    </DL><p>\n");
        foreach (var c in store.Other.Children)
        {
            WriteNode(sb, c, 1);
        }
        sb.Append("</DL><p>\n");
        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, BookmarkNode n, int depth)
    {
        var indent = new string(' ', depth * 4);
        if (n.IsFolder)
        {
            sb.Append(indent).Append("<DT><H3 ADD_DATE=\"").Append(UnixSeconds(n.DateAdded))
              .Append("\" LAST_MODIFIED=\"0\">").Append(Encode(n.Name)).Append("</H3>\n");
            sb.Append(indent).Append("<DL><p>\n");
            foreach (var c in n.Children)
            {
                WriteNode(sb, c, depth + 1);
            }
            sb.Append(indent).Append("</DL><p>\n");
        }
        else
        {
            sb.Append(indent).Append("<DT><A HREF=\"").Append(Encode(n.Url ?? "")).Append("\" ADD_DATE=\"")
              .Append(UnixSeconds(n.DateAdded)).Append("\">").Append(Encode(n.Name)).Append("</A>\n");
        }
    }

    private static string Encode(string s) => WebUtility.HtmlEncode(s);

    private static long UnixSeconds(long chromeTime)
    {
        if (chromeTime <= 0)
        {
            return 0;
        }
        // Chrome 時間：1601-01-01 起的微秒；Unix 紀元相差 11644473600 秒
        return Math.Max(0, chromeTime / 1_000_000 - 11644473600L);
    }
}
