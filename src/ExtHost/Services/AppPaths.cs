using System.IO;

namespace ExtHost.Services;

/// <summary>
/// 程式資料的存放位置。
/// 預設：%LOCALAPPDATA%\ExtHost
/// 可攜模式：執行檔旁邊放一個名為 "portable" 的空檔案，資料就會存在執行檔旁的 data 資料夾。
/// </summary>
public static class AppPaths
{
    public static string ExeDirectory { get; } = AppContext.BaseDirectory;

    public static bool IsPortable { get; } = File.Exists(Path.Combine(ExeDirectory, "portable"));

    public static string Root { get; } = IsPortable
        ? Path.Combine(ExeDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExtHost");

    /// <summary>WebView2 使用者資料（Cookie、快取、擴充功能狀態）。</summary>
    public static string UserData => Path.Combine(Root, "UserData");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string LogFile => Path.Combine(Root, "exthost.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(UserData);
    }

    public static void Log(string message)
    {
        try
        {
            EnsureCreated();
            var fi = new FileInfo(LogFile);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                File.Delete(LogFile);
            }
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // 記錄失敗不影響程式
        }
    }
}
