using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ExtHost.Services;

namespace ExtHost.Bookmarks;

/// <summary>
/// 「匯入書籤和設定」對話框（仿照 Chrome）：
/// 從偵測到的 Chrome / Edge 設定檔、手動指定的設定檔路徑，或書籤 HTML 檔匯入。
/// </summary>
public sealed class ImportBookmarksWindow : Window
{
    private sealed class HtmlSource
    {
    }

    private sealed class ManualSource
    {
    }

    private readonly IBookmarkHost _host;
    private readonly ComboBox _sourceBox;
    private readonly StackPanel _manualPanel;
    private readonly TextBox _pathBox;
    private readonly TextBlock _hint;
    private readonly Border _formPage;
    private readonly Border _donePage;
    private readonly TextBlock _doneText;
    private readonly CheckBox _showBarToggle;

    public ImportBookmarksWindow(IBookmarkHost host)
    {
        _host = host;
        Title = "匯入書籤和設定";
        Owner = host.OwnerWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        ShowInTaskbar = false;
        Background = Res<Brush>("SurfaceBrush");
        FontFamily = Res<FontFamily>("UiFont");
        UseLayoutRounding = true;

        var root = new Grid();

        // ===== 選擇來源 =====
        var form = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        form.Children.Add(new TextBlock
        {
            Text = "匯入書籤和設定",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("TextBrush"),
            Margin = new Thickness(0, 0, 0, 14),
        });

        form.Children.Add(Label("來源"));
        _sourceBox = new ComboBox { Height = 32, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        form.Children.Add(_sourceBox);

        form.Children.Add(Label("選取要匯入的項目："));
        form.Children.Add(new CheckBox
        {
            Content = "我的最愛/書籤",
            IsChecked = true,
            IsEnabled = false,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 0),
        });

        // 手動指定路徑（附導引）
        _manualPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        var guide = new Border
        {
            Background = Res<Brush>("InfoSoftBrush"),
            BorderBrush = Res<Brush>("InfoBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 10),
        };
        var guideText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Res<Brush>("InfoTextBrush"), LineHeight = 20 };
        guideText.Inlines.Add(new Run("如何找到設定檔路徑：") { FontWeight = FontWeights.SemiBold });
        guideText.Inlines.Add(new LineBreak());
        guideText.Inlines.Add(new Run("1. 在 Chrome 網址列輸入 "));
        guideText.Inlines.Add(Code("chrome://version"));
        guideText.Inlines.Add(new Run("（Edge 請輸入 "));
        guideText.Inlines.Add(Code("edge://version"));
        guideText.Inlines.Add(new Run("）"));
        guideText.Inlines.Add(new LineBreak());
        guideText.Inlines.Add(new Run("2. 找到「設定檔路徑」這一行，複製整行路徑"));
        guideText.Inlines.Add(new LineBreak());
        guideText.Inlines.Add(new Run("3. 貼到下方欄位；也可以按「瀏覽…」直接選擇該資料夾裡的 Bookmarks 檔案"));
        guide.Child = guideText;
        _manualPanel.Children.Add(guide);

        var pathRow = new DockPanel();
        var browse = new Button { Content = "瀏覽…", Style = Res<Style>("SecondaryButton"), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => BrowseBookmarksFile();
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        _pathBox = new TextBox { Style = Res<Style>("DialogTextBox") };
        pathRow.Children.Add(_pathBox);
        _manualPanel.Children.Add(pathRow);
        form.Children.Add(_manualPanel);

        _hint = new TextBlock
        {
            FontSize = 12,
            Foreground = Res<Brush>("SubtleBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        form.Children.Add(_hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", Style = Res<Style>("SecondaryButton"), IsCancel = true };
        buttons.Children.Add(cancel);
        var import = new Button { Content = "匯入", Style = Res<Style>("PrimaryButton"), Height = 32, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        import.Click += (_, _) => DoImport();
        buttons.Children.Add(import);
        form.Children.Add(buttons);

        _formPage = new Border { Child = form };
        root.Children.Add(_formPage);

        // ===== 完成 =====
        var done = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        var doneTitle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        doneTitle.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = Res<FontFamily>("IconFont"),
            FontSize = 18,
            Foreground = Res<Brush>("OkTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        doneTitle.Children.Add(new TextBlock { Text = "已完成！", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        done.Children.Add(doneTitle);
        _doneText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res<Brush>("MutedBrush") };
        done.Children.Add(_doneText);
        _showBarToggle = new CheckBox
        {
            Content = "顯示書籤列",
            Style = Res<Style>("ToggleSwitch"),
            FontSize = 13,
            Margin = new Thickness(0, 16, 0, 0),
        };
        done.Children.Add(_showBarToggle);
        var doneButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var finish = new Button { Content = "完成", Style = Res<Style>("PrimaryButton"), Height = 32, IsDefault = true };
        finish.Click += (_, _) =>
        {
            if ((_showBarToggle.IsChecked == true) != _host.IsBookmarkBarPinned)
            {
                _host.ToggleBookmarkBar();
            }
            Close();
        };
        doneButtons.Children.Add(finish);
        done.Children.Add(doneButtons);
        _donePage = new Border { Child = done, Visibility = Visibility.Collapsed };
        root.Children.Add(_donePage);

        Content = root;

        FillSources();
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res<Brush>("MutedBrush"),
        Margin = new Thickness(0, 12, 0, 4),
    };

    private Run Code(string text) => new(text) { FontFamily = Res<FontFamily>("MonoFont"), FontWeight = FontWeights.SemiBold };

    private void FillSources()
    {
        List<BrowserProfile> profiles;
        try
        {
            profiles = BookmarkImporter.DetectProfiles();
        }
        catch (Exception ex)
        {
            AppPaths.Log("偵測瀏覽器設定檔失敗：" + ex.Message);
            profiles = new List<BrowserProfile>();
        }

        foreach (var p in profiles)
        {
            _sourceBox.Items.Add(new ComboBoxItem { Content = p.DisplayName, Tag = p, ToolTip = p.ProfileDir });
        }
        _sourceBox.Items.Add(new ComboBoxItem { Content = "書籤 HTML 檔案", Tag = new HtmlSource() });
        _sourceBox.Items.Add(new ComboBoxItem { Content = "手動指定 Chrome / Edge 設定檔…", Tag = new ManualSource() });

        _sourceBox.SelectionChanged += (_, _) => UpdateSourceUi();
        _sourceBox.SelectedIndex = profiles.Count > 0 ? 0 : _sourceBox.Items.Count - 1;
        if (profiles.Count == 0)
        {
            _hint.Text = "在這台電腦上找不到 Chrome 或 Edge 的設定檔，請依上方說明手動指定，或改用書籤 HTML 檔案。";
        }
        UpdateSourceUi();
    }

    private object? SelectedSource => (_sourceBox.SelectedItem as ComboBoxItem)?.Tag;

    private void UpdateSourceUi()
    {
        var src = SelectedSource;
        _manualPanel.Visibility = src is ManualSource ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = src switch
        {
            BrowserProfile p => "設定檔位置：" + p.ProfileDir,
            HtmlSource => "按「匯入」後選擇書籤 HTML 檔（在 Chrome 的書籤管理員選「匯出書籤」即可產生）。",
            _ => _hint.Text.StartsWith("在這台電腦上找不到", StringComparison.Ordinal) ? _hint.Text : "",
        };
        if (src is ManualSource)
        {
            Dispatcher.BeginInvoke(() => _pathBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void BrowseBookmarksFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選擇 Chrome / Edge 設定檔資料夾中的 Bookmarks 檔案",
            Filter = "書籤檔案 (Bookmarks)|Bookmarks;Account Bookmarks|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
        };
        var start = BookmarkImporter.ChromeUserDataDir;
        if (Directory.Exists(start))
        {
            dlg.InitialDirectory = start;
        }
        if (dlg.ShowDialog(this) == true)
        {
            _pathBox.Text = dlg.FileName;
        }
    }

    private void DoImport()
    {
        ImportedBookmarks data;
        try
        {
            switch (SelectedSource)
            {
                case BrowserProfile p:
                    data = BookmarkImporter.ReadChromeFiles(BookmarkImporter.BookmarkFilesIn(p.ProfileDir), p.Browser);
                    break;
                case HtmlSource:
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "選擇書籤 HTML 檔",
                        Filter = "HTML 檔案 (*.html;*.htm)|*.html;*.htm|所有檔案 (*.*)|*.*",
                        CheckFileExists = true,
                    };
                    if (dlg.ShowDialog(this) != true)
                    {
                        return;
                    }
                    data = BookmarkImporter.ReadHtmlFile(dlg.FileName);
                    break;
                }
                case ManualSource:
                {
                    var files = BookmarkImporter.ResolveManualPath(_pathBox.Text);
                    var source = files[0].Contains(@"\Microsoft\Edge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" : "Google Chrome";
                    data = BookmarkImporter.ReadChromeFiles(files, source);
                    break;
                }
                default:
                    return;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("匯入書籤失敗：" + ex);
            MessageBox.Show(this, "無法讀取書籤：\n" + ex.Message, "匯入書籤", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (data.IsEmpty)
        {
            MessageBox.Show(this, "這個來源沒有任何書籤。", "匯入書籤", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var urls = data.UrlCount;
        var folders = data.FolderCount;
        try
        {
            BookmarkImporter.ImportInto(_host.Bookmarks, data);
        }
        catch (BookmarkSaveException ex)
        {
            MessageBox.Show(this, ex.Message, "匯入書籤", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var from = data.SourceName.Length > 0 ? $"從 {data.SourceName} " : "";
        _doneText.Text = folders > 0
            ? $"已{from}匯入 {urls} 個書籤和 {folders} 個資料夾。"
            : $"已{from}匯入 {urls} 個書籤。";
        _showBarToggle.IsChecked = true;
        _formPage.Visibility = Visibility.Collapsed;
        _donePage.Visibility = Visibility.Visible;
    }
}
