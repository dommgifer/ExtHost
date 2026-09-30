using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ExtHost.Services;

namespace ExtHost.Bookmarks;

/// <summary>
/// 編輯書籤 / 新增網頁 / 重新命名資料夾 / 新增資料夾 對話框（對應 Chrome 的「編輯書籤」視窗）。
/// </summary>
public sealed class BookmarkEditorWindow : Window
{
    private readonly IBookmarkHost _host;
    private readonly bool _isFolder;
    private readonly TextBox _nameBox;
    private readonly TextBox? _urlBox;
    private readonly TreeView? _tree;
    private readonly Button _saveButton;

    private BookmarkEditorWindow(IBookmarkHost host, string title, bool isFolder, string name, string? url, BookmarkNode? selectedFolder, Window owner,
        bool folderTree = false)
    {
        _host = host;
        _isFolder = isFolder;

        Title = title;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 480;
        ShowInTaskbar = false;
        Background = Res<Brush>("SurfaceBrush");
        FontFamily = Res<FontFamily>("UiFont");
        UseLayoutRounding = true;

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

        root.Children.Add(Label("名稱"));
        _nameBox = new TextBox { Style = Res<Style>("DialogTextBox"), Text = name };
        _nameBox.TextChanged += (_, _) => Validate();
        root.Children.Add(_nameBox);

        if (!isFolder)
        {
            root.Children.Add(Label("網址"));
            _urlBox = new TextBox { Style = Res<Style>("DialogTextBox"), Text = url ?? "" };
            _urlBox.TextChanged += (_, _) => Validate();
            root.Children.Add(_urlBox);
        }

        if (!isFolder || folderTree)
        {
            _tree = new TreeView
            {
                Height = 220,
                Margin = new Thickness(0, 14, 0, 0),
                BorderBrush = Res<Brush>("BorderStrongBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
            };
            root.Children.Add(_tree);
            BuildTree(selectedFolder ?? host.Bookmarks.BookmarkBar);
        }

        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        if (_tree != null)
        {
            var newFolder = new Button { Content = "新增資料夾", Style = Res<Style>("SecondaryButton") };
            newFolder.Click += (_, _) => NewFolderInTree();
            DockPanel.SetDock(newFolder, Dock.Left);
            buttons.Children.Add(newFolder);
        }
        _saveButton = new Button
        {
            Content = "儲存",
            Style = Res<Style>("PrimaryButton"),
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
        };
        _saveButton.Click += (_, _) => DialogResult = true;
        DockPanel.SetDock(_saveButton, Dock.Right);
        buttons.Children.Add(_saveButton);
        var cancel = new Button { Content = "取消", Style = Res<Style>("SecondaryButton"), IsCancel = true };
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Validate();

        Loaded += (_, _) =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
        };
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res<Brush>("MutedBrush"),
        Margin = new Thickness(0, 10, 0, 4),
    };

    private string NameText => _nameBox.Text.Trim();

    private string? FixedUrl => _urlBox == null ? null : UrlHelper.FixupUrl(_urlBox.Text);

    private BookmarkNode? SelectedFolder => (_tree?.SelectedItem as TreeViewItem)?.Tag as BookmarkNode;

    private void Validate()
    {
        if (_saveButton == null)
        {
            return;
        }
        _saveButton.IsEnabled = _isFolder
            ? NameText.Length > 0 && (_tree == null || SelectedFolder != null)
            : FixedUrl != null && SelectedFolder != null;
    }

    // ======================= 資料夾樹 =======================

    private void BuildTree(BookmarkNode select)
    {
        _tree!.Items.Clear();
        foreach (var r in _host.Bookmarks.Roots)
        {
            _tree.Items.Add(MakeTreeItem(r, select));
        }
        _tree.SelectedItemChanged += (_, _) => Validate();
    }

    private TreeViewItem MakeTreeItem(BookmarkNode folder, BookmarkNode select)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(BookmarkUi.MakeGlyph(BookmarkUi.FolderGlyph));
        header.Children.Add(new TextBlock { Text = folder.Name, Margin = new Thickness(6, 0, 0, 0), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var item = new TreeViewItem
        {
            Header = header,
            Tag = folder,
            Padding = new Thickness(2, 3, 2, 3),
            IsExpanded = folder.IsRoot || select.IsDescendantOf(folder),
        };
        foreach (var c in folder.Children.Where(c => c.IsFolder))
        {
            item.Items.Add(MakeTreeItem(c, select));
        }
        if (folder == select)
        {
            item.IsSelected = true;
            item.Loaded += (_, _) => item.BringIntoView();
        }
        return item;
    }

    private void NewFolderInTree()
    {
        var parent = SelectedFolder ?? _host.Bookmarks.BookmarkBar;
        var created = PromptFolder(_host, "新增資料夾", "新資料夾", this);
        if (created == null)
        {
            return;
        }
        var folder = _host.Bookmarks.AddFolder(parent, -1, created);
        _tree!.Items.Clear();
        foreach (var r in _host.Bookmarks.Roots)
        {
            _tree.Items.Add(MakeTreeItem(r, folder));
        }
        Validate();
    }

    // ======================= 對外 API =======================

    private static string? PromptFolder(IBookmarkHost host, string title, string name, Window owner)
    {
        var dlg = new BookmarkEditorWindow(host, title, true, name, null, null, owner);
        return dlg.ShowDialog() == true ? dlg.NameText : null;
    }

    public static void EditUrl(IBookmarkHost host, BookmarkNode node, Window? owner = null)
    {
        var dlg = new BookmarkEditorWindow(host, "編輯書籤", false, node.Name, node.Url, node.Parent, owner ?? host.OwnerWindow);
        if (dlg.ShowDialog() != true || node.Parent == null)
        {
            return;
        }
        host.Bookmarks.Update(node, dlg.NameText, dlg.FixedUrl);
        var folder = dlg.SelectedFolder;
        if (folder != null && folder != node.Parent)
        {
            host.Bookmarks.Move(node, folder, -1);
        }
    }

    public static void AddUrl(IBookmarkHost host, BookmarkNode parent, int index)
    {
        var dlg = new BookmarkEditorWindow(host, "新增書籤", false, "", "", parent, host.OwnerWindow);
        if (dlg.ShowDialog() != true)
        {
            return;
        }
        var folder = dlg.SelectedFolder ?? parent;
        host.Bookmarks.AddUrl(folder, folder == parent ? index : -1, dlg.NameText, dlg.FixedUrl!);
    }

    /// <summary>「將所有分頁加入書籤」：輸入資料夾名稱並選擇放在哪裡。</summary>
    public static (string Name, BookmarkNode Parent)? PromptNewFolderWithTree(IBookmarkHost host, string title, BookmarkNode selected)
    {
        var dlg = new BookmarkEditorWindow(host, title, true, "", null, selected, host.OwnerWindow, folderTree: true);
        if (dlg.ShowDialog() != true || dlg.SelectedFolder == null)
        {
            return null;
        }
        return (dlg.NameText, dlg.SelectedFolder);
    }

    public static void EditFolder(IBookmarkHost host, BookmarkNode folder)
    {
        var name = PromptFolder(host, "編輯資料夾名稱", folder.Name, host.OwnerWindow);
        if (name != null)
        {
            host.Bookmarks.Update(folder, name, null);
        }
    }

    public static void AddFolder(IBookmarkHost host, BookmarkNode parent, int index)
    {
        var name = PromptFolder(host, "新增資料夾", "新資料夾", host.OwnerWindow);
        if (name != null)
        {
            host.Bookmarks.AddFolder(parent, index, name);
        }
    }
}
