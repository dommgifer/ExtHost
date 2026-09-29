using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ExtHost.Services;

namespace ExtHost;

public partial class App : Application
{
    public const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppPaths.Log("未處理例外：" + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppPaths.Log("未觀察的工作例外：" + args.Exception);
            args.SetObserved();
        };

        AppPaths.EnsureCreated();

        if (BrowserEnvironment.RuntimeVersion() == null)
        {
            var result = MessageBox.Show(
                "這台電腦沒有安裝 Microsoft Edge WebView2 Runtime，ExtHost 需要它才能執行。\n\n要開啟下載頁面嗎？",
                "ExtHost", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(WebView2DownloadUrl) { UseShellExecute = true });
                }
                catch
                {
                }
            }
            Shutdown(1);
            return;
        }

        Settings = AppSettings.Load();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 書籤存檔失敗（修改已還原）：顯示原因即可，不是程式錯誤
        if (e.Exception is BookmarkSaveException bse)
        {
            if (MainWindow is { IsVisible: true } owner)
            {
                MessageBox.Show(owner, bse.Message, "書籤", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(bse.Message, "書籤", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            e.Handled = true;
            return;
        }
        AppPaths.Log("UI 例外：" + e.Exception);
        MessageBox.Show("發生未預期的錯誤：\n" + e.Exception.Message + "\n\n詳細內容已寫入：" + AppPaths.LogFile,
            "ExtHost", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
