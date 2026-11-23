namespace DeskMate.Core.Models;

/// <summary>
/// 应用配置，落盘于 <c>%APPDATA%\DeskMate\config.json</c>。
/// 新增字段必须保留默认值，保证旧配置文件能直接反序列化。
/// </summary>
public class AppConfig
{
    /// <summary>Dock 图标边长可调下限。设置页滑条与各服务的钳制共用这一对常量。</summary>
    public const int DockIconSizeMin = 24;

    /// <summary>Dock 图标边长可调上限。</summary>
    public const int DockIconSizeMax = 80;

    /// <summary>
    /// 设置页「小 / 中 / 大」三档的边长（DIP）。中档就是出厂默认：
    /// 滑条留着做细调，多数人点一档就够了。
    /// </summary>
    public const int DockIconSizeSmall = 32;

    public const int DockIconSizeMedium = 44;

    public const int DockIconSizeLarge = 60;

    /// <summary>任务栏遮罩宽度可调范围（DIP）。下限要够盖住开始按钮，上限不到半屏，免得盖到托盘。</summary>
    public const int TaskbarCoverWidthMin = 160;

    /// <summary>任务栏遮罩宽度上限。</summary>
    public const int TaskbarCoverWidthMax = 900;

    /// <summary>
    /// 遮住任务栏左边那一坨（开始按钮、搜索、任务视图、应用图标）。
    /// 是「盖住」不是「关掉」：DeskMate 自己画一条同材质的条压在上面，任务栏进程与右侧托盘、时钟都不受影响，
    /// 也不写任何系统设置。
    /// </summary>
    public bool TaskbarCoverEnabled { get; set; } = false;

    /// <summary>遮罩宽度（DIP），从屏幕左下角往右量。</summary>
    public int TaskbarCoverWidth { get; set; } = 420;

    /// <summary>
    /// 用自建任务栏顶替系统任务栏：DeskMate 在屏幕底边注册一条自己的 AppBar
    /// （时间 · 输入法 · 网络 · 音量），并把系统任务栏切到自动隐藏 —— 这是正规的全局设置，
    /// 退出 DeskMate 时按接管前的状态恢复。开着时与「任务栏遮罩」互斥。
    /// 托盘图标不迁移（需要原生任务栏时把鼠标移到屏幕最底边唤出）。
    /// </summary>
    public bool TaskbarBarEnabled { get; set; } = false;

    /// <summary>
    /// 「保留托盘」模式（只在 <see cref="TaskbarBarEnabled"/> 开着时生效）：不隐藏整个任务栏，
    /// 而是把原生任务栏窗口<b>裁</b>成只剩右侧托盘那一段（SetWindowRgn，不是遮挡）——
    /// 中间的应用图标物理上不存在，动态壁纸原样透出来；时钟、托盘图标、输入法指示照常可用。
    /// 此模式下右下角自建小条不显示（托盘已有时钟/输入法/网络/音量），自动隐藏保持用户自己的设置。
    /// </summary>
    public bool TaskbarKeepTray { get; set; } = false;

    /// <summary>开机自启。</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>呼出全局搜索的快捷键。</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>
    /// 呼出启动台的快捷键。默认不绑定 —— 一个桌面工具不该未经同意就抢走系统按键，
    /// 需要的人在设置页里点一下即可；不绑也能从 Dock、托盘菜单进启动台。
    /// </summary>
    public string LauncherHotkey { get; set; } = "";

    /// <summary>呼出文件夹管理面板的快捷键，同样默认不绑定。</summary>
    public string FolderHotkey { get; set; } = "";

    /// <summary>Light / Dark / System。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Bottom / Left / Right。</summary>
    public string DockPosition { get; set; } = "Bottom";

    /// <summary>Dock 图标边长（DIP），可调 24~80，默认取「中」那一档。</summary>
    public int DockIconSize { get; set; } = DockIconSizeMedium;

    /// <summary>Dock 背景不透明度，0~1。</summary>
    public double DockOpacity { get; set; } = 0.9;

    /// <summary>是否启用毛玻璃。</summary>
    public bool BlurEnabled { get; set; } = true;

    /// <summary>是否自动隐藏 Dock。</summary>
    public bool AutoHideDock { get; set; } = false;

    /// <summary>贴桌面层（实验性）。</summary>
    public bool DesktopLayerMode { get; set; } = false;

    /// <summary>
    /// 让 Dock 贴住所在边的屏幕边缘（不留常规的 8px 缝隙）。底部停靠时就是压在任务栏那一条上，
    /// 水平居中正好落在任务栏图标靠左剩下的空白中段。
    /// 只影响 DeskMate 自己的窗口位置 —— 任务栏要不要自动隐藏由用户在 Windows 设置里自己决定，
    /// DeskMate 不改任何系统设置。
    /// </summary>
    public bool DockFlushToEdge { get; set; } = false;

    /// <summary>首次运行是否已做过桌面文件夹导入询问。</summary>
    public bool DesktopScanPrompted { get; set; } = false;

    /// <summary>
    /// 启动台的自定义快捷方式文件夹，只支持一个。
    /// 留空表示用默认目录（%APPDATA%\DeskMate\Launchpad）—— 默认放在自己的数据目录里，
    /// 这样「手动添加应用」只是往 DeskMate 自己的文件夹放一个快捷方式，不会往用户别处写文件。
    /// </summary>
    public string LaunchpadFolder { get; set; } = "";

    /// <summary>用户在启动台里手动隐藏的条目（存 AppInfo.Id，即解析后的目标路径）。</summary>
    public List<string> HiddenApps { get; set; } = new();

    /// <summary>Dock 项。</summary>
    public List<DockItem> DockItems { get; set; } = new();

    /// <summary>受管文件夹（仅路径引用）。</summary>
    public List<FolderItem> Folders { get; set; } = new();

    /// <summary>文件夹分组。</summary>
    public List<FolderGroup> Groups { get; set; } = new();

    /// <summary>
    /// 收藏的路径引用（目录、文件、压缩包都收，只记路径）。
    /// 收藏不复制、不移动、不改名任何文件，取消收藏也只是把这一行从配置里去掉。
    /// </summary>
    public List<string> Favorites { get; set; } = new();

    /// <summary>
    /// 浏览列表的排序列（Name / Type / Size / Date，大小写不敏感，坏值回落 Name）。
    /// 是全局偏好：在所有文件夹里都按同一列排，不用每层重选一遍。
    /// </summary>
    public string BrowseSortBy { get; set; } = "Name";

    /// <summary>浏览列表是否降序。</summary>
    public bool BrowseSortDescending { get; set; } = false;
}
