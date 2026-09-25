using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ExtHost.Tabs;

public abstract class TabBase : INotifyPropertyChanged
{
    private string _title = "新分頁";
    private ImageSource? _icon;
    private bool _isLoading;
    private string _url = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>分頁內容（加入主視窗的內容區）。</summary>
    public abstract FrameworkElement View { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _icon != null;

    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    /// <summary>分頁圖示預設字型符號（Segoe Fluent Icons）。</summary>
    public virtual string Glyph => "";

    public virtual void OnActivated()
    {
    }

    public virtual void Close()
    {
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
