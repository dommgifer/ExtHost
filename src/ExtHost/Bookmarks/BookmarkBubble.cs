using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ExtHost.Services;

namespace ExtHost.Bookmarks;

/// <summary>
/// 按網址列星號（或 Ctrl+D）後出現的「已加入書籤」小視窗，與 Chrome 相同：
/// 按下星號時就已經加入書籤，小視窗只是用來修改名稱、資料夾或移除。
/// </summary>
public sealed class BookmarkBubble : Window
{
    private const double ShadowMargin = 12;
    private const double BubbleWidth = 340;

    private readonly IBookmarkHost _host;
    private readonly BookmarkNode _node;
    private readonly TextBox _nameBox;
    private readonly ComboBox _folderBox;
    private readonly Action<BookmarkNode> _rememberFolder;
    private bool _closing;
    private bool _applyOnClose = true;
    private Action? _afterClose;

    public static BookmarkBubble? Current { get; private set; }

    private static DateTime _lastClosedAt;

    private BookmarkBubble(IBookmarkHost host, BookmarkNode node, bool isNew, Action<BookmarkNode> rememberFolder)
    {
        _host = host;
        _node = node;
        _rememberFolder = rememberFolder;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        SizeToContent = SizeToContent.Height;
        Width = BubbleWidth + ShadowMargin * 2;
        FontFamily = Res<FontFamily>("UiFont");
        UseLayoutRounding = true;
        Title = isNew ? "已加入書籤" : "編輯書籤";

        var card = new Border
        {
            Margin = new Thickness(ShadowMargin),
            Background = Res<Brush>("SurfaceBrush"),
            BorderBrush = Res<Brush>("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 12, 18, 16),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Direction = 270, Opacity = 0.18 },
        };

        var root = new StackPanel();

        // 標題列
        var header = new DockPanel { Margin = new Thickness(0, 0, -8, 10) };
        var close = new Button
        {
            Style = Res<Style>("AddressIconButton"),
            Content = "",
            FontSize = 10,
            ToolTip = "關閉",
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res<Brush>("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(header);

        // 名稱、資料夾
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        form.Children.Add(FormLabel("名稱", 0));
        _nameBox = new TextBox { Style = Res<Style>("DialogTextBox"), Text = node.Name };
        Grid.SetRow(_nameBox, 0);
        Grid.SetColumn(_nameBox, 1);
        form.Children.Add(_nameBox);

        form.Children.Add(FormLabel("資料夾", 2));
        _folderBox = new ComboBox { Height = 32, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetRow(_folderBox, 2);
        Grid.SetColumn(_folderBox, 1);
        form.Children.Add(_folderBox);
        FillFolders();
        root.Children.Add(form);

        // 按鈕
        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var more = new Button { Content = "更多…", Style = Res<Style>("SecondaryButton") };
        more.Click += (_, _) => OpenFullEditor();
        DockPanel.SetDock(more, Dock.Left);
        buttons.Children.Add(more);
        var done = new Button { Content = "完成", Style = Res<Style>("PrimaryButton"), Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        done.Click += (_, _) => Close();
        DockPanel.SetDock(done, Dock.Right);
        buttons.Children.Add(done);
        var remove = new Button { Content = "移除", Style = Res<Style>("SecondaryButton") };
        remove.Click += (_, _) => Remove();
        DockPanel.SetDock(remove, Dock.Right);
        buttons.Children.Add(remove);
        root.Children.Add(buttons);

        card.Child = root;
        Content = card;

        Loaded += (_, _) =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
        };
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            if (Current == this)
            {
                Current = null;
            }
            _lastClosedAt = DateTime.UtcNow;
            _afterClose?.Invoke();
        };
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private TextBlock FormLabel(string text, int row)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = Res<Brush>("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        Grid.SetRow(t, row);
        return t;
    }

    private sealed class ChooseOther
    {
    }

    private void FillFolders()
    {
        foreach (var (folder, depth) in _host.Bookmarks.AllFolders())
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * 14, 0, 0, 0) };
            sp.Children.Add(BookmarkUi.MakeGlyph(BookmarkUi.FolderGlyph));
            sp.Children.Add(new TextBlock { Text = folder.Name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var item = new ComboBoxItem { Content = sp, Tag = folder };
            _folderBox.Items.Add(item);
            if (folder == _node.Parent)
            {
                _folderBox.SelectedItem = item;
            }
        }
        _folderBox.Items.Add(new ComboBoxItem { Content = new Separator { Margin = new Thickness(0) }, IsEnabled = false, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _folderBox.Items.Add(new ComboBoxItem { Content = "選擇其他資料夾…", Tag = new ChooseOther() });

        _folderBox.SelectionChanged += (_, _) =>
        {
            if ((_folderBox.SelectedItem as ComboBoxItem)?.Tag is ChooseOther)
            {
                OpenFullEditor();
            }
        };
    }

    private BookmarkNode? SelectedFolder => (_folderBox.SelectedItem as ComboBoxItem)?.Tag as BookmarkNode;

    // ======================= 動作 =======================

    private void Apply()
    {
        if (_node.Parent == null)
        {
            return; // 已被刪除
        }
        var name = _nameBox.Text.Trim();
        if (name != _node.Name)
        {
            _host.Bookmarks.Update(_node, name, null);
        }
        var folder = SelectedFolder;
        if (folder != null && folder != _node.Parent)
        {
            _host.Bookmarks.Move(_node, folder, -1);
        }
        if (_node.Parent != null)
        {
            _rememberFolder(_node.Parent);
        }
    }

    private void Remove()
    {
        _applyOnClose = false;
        // 與 Chrome 相同：移除這個網址的所有書籤
        foreach (var n in _host.Bookmarks.FindAllByUrl(_node.Url!))
        {
            _host.Bookmarks.Remove(n);
        }
        Close();
    }

    private void OpenFullEditor()
    {
        Apply();
        _applyOnClose = false;
        var host = _host;
        var node = _node;
        _afterClose = () => host.OwnerWindow.Dispatcher.BeginInvoke(() =>
        {
            if (node.Parent != null)
            {
                BookmarkEditorWindow.EditUrl(host, node);
            }
        });
        Close();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc：不套用修改，書籤保留
            _applyOnClose = false;
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && !_folderBox.IsDropDownOpen)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && !IsActive)
            {
                Close();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            _closing = true;
            if (_applyOnClose)
            {
                Apply();
            }
        }
        base.OnClosing(e);
    }

    // ======================= 顯示 =======================

    /// <summary>
    /// 星號 / Ctrl+D：還沒加入書籤就加入並顯示小視窗；已加入就顯示編輯小視窗。
    /// 小視窗開著時再按一次則關閉。
    /// </summary>
    public static void Toggle(IBookmarkHost host, FrameworkElement anchor, string url, string title,
        BookmarkNode defaultFolder, Action<BookmarkNode> rememberFolder)
    {
        if (Current != null)
        {
            Current.Close();
            return;
        }
        // 點星號時小視窗會先因失去焦點而關閉，這時不要重新打開
        if ((DateTime.UtcNow - _lastClosedAt).TotalMilliseconds < 300)
        {
            return;
        }

        var node = host.Bookmarks.FindByUrl(url);
        var isNew = node == null;
        node ??= host.Bookmarks.AddUrl(defaultFolder, -1, title, url);

        var bubble = new BookmarkBubble(host, node, isNew, rememberFolder) { Owner = host.OwnerWindow };
        Current = bubble;

        // 靠右對齊星號下方
        var source = PresentationSource.FromVisual(anchor);
        var bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        if (source?.CompositionTarget != null)
        {
            bottomRight = source.CompositionTarget.TransformFromDevice.Transform(bottomRight);
        }
        var work = SystemParameters.WorkArea;
        var left = bottomRight.X - bubble.Width + ShadowMargin + 8;
        bubble.Left = Math.Max(work.Left, Math.Min(left, work.Right - bubble.Width));
        bubble.Top = bottomRight.Y + 6 - ShadowMargin;
        bubble.Show();
    }
}
