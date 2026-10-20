using System.Windows;

namespace DeskMate.App.Views;

/// <summary>
/// 首次启动的欢迎窗：三条上手提示，外加「把桌面上的文件夹加入管理」。
/// <para>
/// 以前首次启动只有一个「要不要导入桌面文件夹」的是/否框，点错就再也没有入口了；
/// 现在导入这件事在文件夹面板的工具栏上一直留着，欢迎窗也随时能从托盘「使用说明」再打开。
/// </para>
/// </summary>
public partial class WelcomeWindow : Window
{
    /// <summary>构造欢迎窗。</summary>
    /// <param name="desktopFolders">桌面上还没纳入管理的文件夹，空则不显示导入那一块。</param>
    public WelcomeWindow(IReadOnlyList<string> desktopFolders)
    {
        DesktopLine = $"检测到桌面有 {desktopFolders.Count} 个文件夹。";
        DesktopVisibility = desktopFolders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportVisibility = desktopFolders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CloseVisibility = desktopFolders.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        FoldersToImport = desktopFolders;

        InitializeComponent();
        DataContext = this;
    }

    /// <summary>用户点了「加入管理」时要导入的路径；没点就是空。</summary>
    public IReadOnlyList<string> FoldersToImport { get; private set; } = [];

    /// <summary>导入那一块的说明文字。</summary>
    public string DesktopLine { get; }

    /// <summary>桌面有没有文件夹可导入。</summary>
    public Visibility DesktopVisibility { get; }

    /// <summary>「加入管理」按钮的显示控制。</summary>
    public Visibility ImportVisibility { get; }

    /// <summary>没东西可导入时，只留一个「开始使用」。</summary>
    public Visibility CloseVisibility { get; }

    private void OnImport(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnClose(object sender, RoutedEventArgs e)
    {
        // 没点导入就不要把列表带回去。
        FoldersToImport = [];
        DialogResult = true;
    }
}
