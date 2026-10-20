using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMate.App.Services;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.ViewModels;

/// <summary>
/// 设置页视图模型。左侧导航 + 右侧三块：通用 / 外观 / 数据。
/// 改动即时写回 config.json，靠 IConfigService.ConfigChanged 让各窗口实时刷新。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const int SliderSaveDebounceMs = 220;

    private readonly IConfigService _config;
    private readonly IAutoStartService _autoStart;
    private readonly IAppScannerService _scanner;
    private readonly IAppLauncherService _launcher;
    private readonly IUsageService _usage;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly DispatcherTimer _sliderDebounce;

    private bool _loading;

    /// <summary>构造设置视图模型。</summary>
    public SettingsViewModel(
        IConfigService config,
        IAutoStartService autoStart,
        IAppScannerService scanner,
        IAppLauncherService launcher,
        IUsageService usage,
        ILogger<SettingsViewModel> logger)
    {
        _config = config;
        _autoStart = autoStart;
        _scanner = scanner;
        _launcher = launcher;
        _usage = usage;
        _logger = logger;

        _sliderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SliderSaveDebounceMs) };
        _sliderDebounce.Tick += OnSliderDebounceTick;
    }

    /// <summary>需要用户选导出路径。</summary>
    public event EventHandler? ExportRequested;

    /// <summary>需要用户选导入文件。</summary>
    public event EventHandler? ImportRequested;

    /// <summary>需要用户选一个本地目录作为启动台快捷方式文件夹。</summary>
    public event EventHandler? PickLaunchpadFolderRequested;

    /// <summary>提示文本。</summary>
    public event EventHandler<NoticeEventArgs>? UserNotice;

    [ObservableProperty]
    private bool _isGeneralPage = true;

    [ObservableProperty]
    private bool _isAppearancePage;

    [ObservableProperty]
    private bool _isDataPage;

    [ObservableProperty]
    private bool _autoStartEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyDisplay))]
    private string _hotkeyText = "Alt+Space";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LauncherHotkeyDisplay))]
    private string _launcherHotkeyText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderHotkeyDisplay))]
    private string _folderHotkeyText = string.Empty;

    [ObservableProperty]
    private bool _isCapturingSearch;

    [ObservableProperty]
    private bool _isCapturingLauncher;

    [ObservableProperty]
    private bool _isCapturingFolder;

    /// <summary>正在捕获哪一个入口的快捷键，空串表示没在捕获。</summary>
    private string _capturingSlot = string.Empty;

    /// <summary>三个入口的槽位名，窗口只认这个，不关心具体是哪个框。</summary>
    public const string SearchSlot = "search";

    /// <summary>启动台快捷键槽位。</summary>
    public const string LauncherSlot = "launcher";

    /// <summary>文件夹面板快捷键槽位。</summary>
    public const string FolderSlot = "folder";

    /// <summary>是否正在捕获（窗口据此吞掉按键）。</summary>
    public bool IsCapturingHotkey => _capturingSlot.Length > 0;

    /// <summary>搜索快捷键的框内文本。没绑定时不能留空白，否则看着像控件坏了。</summary>
    public string HotkeyDisplay => UnboundText(HotkeyText);

    /// <summary>启动台快捷键的框内文本。</summary>
    public string LauncherHotkeyDisplay => UnboundText(LauncherHotkeyText);

    /// <summary>文件夹面板快捷键的框内文本。</summary>
    public string FolderHotkeyDisplay => UnboundText(FolderHotkeyText);

    private static string UnboundText(string value)
        => string.IsNullOrWhiteSpace(value) ? "未绑定 · 点一下再按下组合键" : value;

    [ObservableProperty]
    private string _themeOption = "System";

    [ObservableProperty]
    private string _dockPosition = "Bottom";

    [ObservableProperty]
    private int _dockIconSize = 56;

    [ObservableProperty]
    private int _dockOpacityPercent = 90;

    [ObservableProperty]
    private bool _blurEnabled = true;

    [ObservableProperty]
    private bool _autoHideDock;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchpadFolderDisplay))]
    private string _launchpadFolder = string.Empty;

    [ObservableProperty]
    private bool _desktopLayerMode;

    [ObservableProperty]
    private bool _dockFlushToEdge;

    /// <summary>遮住任务栏左边那一坨（开始按钮与应用图标）。</summary>
    [ObservableProperty]
    private bool _taskbarCoverEnabled;

    /// <summary>遮罩宽度（DIP）。</summary>
    [ObservableProperty]
    private int _taskbarCoverWidth;

    /// <summary>启动台快捷方式文件夹的生效路径（配置留空时显示默认目录）。</summary>
    public string LaunchpadFolderDisplay => WellKnownPaths.ResolveLaunchpadFolder(LaunchpadFolder);

    /// <summary>配置文件位置，页面上直接展示给用户。</summary>
    public string ConfigPath => _config.FilePath;

    /// <summary>数据目录。</summary>
    public string DataPath => WellKnownPaths.Root;

    /// <summary>当前配置里有多少东西，导入确认时要说清会被替换掉什么。</summary>
    public string CurrentSummary
        => $"Dock {_config.Current.DockItems.Count} 项、文件夹 {_config.Current.Folders.Count} 个、分组 {_config.Current.Groups.Count} 个";

    /// <summary>把当前配置读进界面（此时不回写，避免载入即触发改动）。</summary>
    public void LoadFromConfig()
    {
        _loading = true;
        try
        {
            var current = _config.Current;

            AutoStartEnabled = _autoStart.IsEnabled();
            HotkeyText = current.Hotkey;
            LauncherHotkeyText = current.LauncherHotkey;
            FolderHotkeyText = current.FolderHotkey;
            ThemeOption = current.Theme;
            DockPosition = current.DockPosition;
            DockIconSize = current.DockIconSize;
            DockOpacityPercent = (int)Math.Round(current.DockOpacity * 100);
            BlurEnabled = current.BlurEnabled;
            AutoHideDock = current.AutoHideDock;
            DesktopLayerMode = current.DesktopLayerMode;
            DockFlushToEdge = current.DockFlushToEdge;
            TaskbarCoverEnabled = current.TaskbarCoverEnabled;
            TaskbarCoverWidth = current.TaskbarCoverWidth;
            LaunchpadFolder = current.LaunchpadFolder;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>切到「通用」。</summary>
    [RelayCommand]
    public void ShowGeneral() => SelectPage(true, false, false);

    /// <summary>切到「外观」。</summary>
    [RelayCommand]
    public void ShowAppearance() => SelectPage(false, true, false);

    /// <summary>切到「数据」。</summary>
    [RelayCommand]
    public void ShowData() => SelectPage(false, false, true);

    /// <summary>点某个快捷键输入框进入捕获模式，由窗口接管按键。槽位见 <see cref="SearchSlot"/>。</summary>
    public void BeginHotkeyCapture(string slot)
    {
        _capturingSlot = slot;
        IsCapturingSearch = slot == SearchSlot;
        IsCapturingLauncher = slot == LauncherSlot;
        IsCapturingFolder = slot == FolderSlot;
    }

    /// <summary>结束捕获。</summary>
    public void EndHotkeyCapture()
    {
        _capturingSlot = string.Empty;
        IsCapturingSearch = false;
        IsCapturingLauncher = false;
        IsCapturingFolder = false;
    }

    /// <summary>捕获到一组按键后写进正在捕获的那个框；真正的注册由 AppShell 完成。</summary>
    public void ApplyCapturedHotkey(string hotkey)
    {
        var slot = _capturingSlot;
        EndHotkeyCapture();

        switch (slot)
        {
            case SearchSlot:
                HotkeyText = hotkey;
                break;
            case LauncherSlot:
                LauncherHotkeyText = hotkey;
                break;
            case FolderSlot:
                FolderHotkeyText = hotkey;
                break;
        }
    }

    /// <summary>「不绑定」：清掉某个入口的快捷键。搜索栏有托盘菜单兜底，所以允许完全解绑。</summary>
    [RelayCommand]
    public void ClearHotkeySlot(string? slot)
    {
        switch (slot)
        {
            case SearchSlot:
                HotkeyText = string.Empty;
                break;
            case LauncherSlot:
                LauncherHotkeyText = string.Empty;
                break;
            case FolderSlot:
                FolderHotkeyText = string.Empty;
                break;
        }

        OnNotice($"{SlotTitle(slot)} 的快捷键已清除，仍可从 Dock 或托盘菜单进入", ToastKind.Info);
    }

    private static string SlotTitle(string? slot) => slot switch
    {
        SearchSlot => "搜索",
        LauncherSlot => "启动台",
        FolderSlot => "文件夹面板",
        _ => "该入口",
    };

    /// <summary>主题选择（单选按钮的命令参数）。</summary>
    [RelayCommand]
    public void SetTheme(string? theme)
    {
        if (!string.IsNullOrWhiteSpace(theme))
        {
            ThemeOption = theme;
        }
    }

    /// <summary>Dock 位置选择。</summary>
    [RelayCommand]
    public void SetDockPosition(string? position)
    {
        if (!string.IsNullOrWhiteSpace(position))
        {
            DockPosition = position;
        }
    }

    /// <summary>请求导出配置（文件对话框由窗口提供）。</summary>
    [RelayCommand]
    public void RequestExport() => ExportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>请求导入配置。</summary>
    [RelayCommand]
    public void RequestImport() => ImportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>打开数据目录（配置、应用缓存、启动统计、日志都在这儿）。</summary>
    [RelayCommand]
    public void OpenDataFolder() => OpenFolder(DataPath);

    /// <summary>打开日志文件夹。</summary>
    [RelayCommand]
    public void OpenLogsFolder() => OpenFolder(WellKnownPaths.LogsFolder);

    private void OpenFolder(string path)
    {
        try
        {
            _launcher.OpenInExplorer(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开数据目录失败 {Path}", path);
            OnNotice($"打不开 {path}：{ex.Message}");
        }
    }

    /// <summary>清空启动次数统计：「常用」页排得不满意时从零开始，不用自己去删 usage.json。</summary>
    [RelayCommand]
    public void ClearUsageStats()
    {
        var cleared = _usage.Reset();

        OnNotice(
            cleared == 0
                ? "本来就没有启动记录。"
                : $"已清空 {cleared} 条启动记录，「常用」页会重新开始积累",
            ToastKind.Success);
    }

    /// <summary>请求选择启动台的快捷方式文件夹（目录选择框由窗口提供）。</summary>
    [RelayCommand]
    public void RequestPickLaunchpadFolder() => PickLaunchpadFolderRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>清空自定义路径，回到默认目录。</summary>
    [RelayCommand]
    public void ClearLaunchpadFolder() => LaunchpadFolder = string.Empty;

    partial void OnLaunchpadFolderChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        _config.Current.LaunchpadFolder = value.Trim().Trim('"');
        _ = SaveAndRescanAsync();
    }

    private async Task SaveAndRescanAsync()
    {
        try
        {
            await _config.SaveAsync();

            // 换了文件夹必须重扫，否则启动台还拿着旧目录的结果。
            await _scanner.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新启动台快捷方式文件夹失败");
            OnNotice($"设置失败：{ex.Message}");
        }
    }

    /// <summary>导出当前配置到用户选择的路径。</summary>
    public async Task ExportToAsync(string targetPath)
    {
        try
        {
            // 先把可能还在防抖中的滑条值落盘，导出的才是界面上的最新状态。
            await FlushAsync();
            File.Copy(ConfigPath, targetPath, overwrite: true);
            OnNotice($"配置已导出到：{targetPath}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出配置失败");
            OnNotice($"导出失败：{ex.Message}");
        }
    }

    /// <summary>从用户选的文件导入配置。</summary>
    public async Task ImportFromAsync(string sourcePath)
    {
        string? backup = null;

        try
        {
            var json = await File.ReadAllTextAsync(sourcePath, System.Text.Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (parsed is null)
            {
                throw new InvalidDataException("文件里没有读到有效的配置内容");
            }

            // 先备份再动。导入错文件、或者中途写盘失败时，用户手里必须还留着原来那一份。
            backup = $"{ConfigPath}.bak-{DateTime.Now:yyyyMMddHHmmss}";
            if (File.Exists(ConfigPath))
            {
                File.Copy(ConfigPath, backup, overwrite: true);
            }

            // 借道写回：先验证过的对象直接落盘，再让服务重新载入，保证各窗口拿到同一份状态。
            await _config.ResetAsync();
            CopyInto(_config.Current, parsed);
            await _config.SaveAsync();
            await _config.LoadAsync();

            LoadFromConfig();
            OnNotice($"配置已导入。原来的配置备份在：{backup}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入配置失败");

            // backup 为 null 说明还没开始动就已经失败，那句话才是真的；
            // 已经开始动了就不能再说「未被改动」，那种时候唯一能给的是备份在哪。
            OnNotice(backup is null
                ? $"导入失败：{ex.Message}\n\n你现在的配置没有被改动。"
                : $"导入中途失败：{ex.Message}\n\n原来的配置已经备份在：{backup}\n导入这个文件、或用它替换 config.json 就能恢复。");
        }
    }

    /// <summary>恢复出厂设置。</summary>
    [RelayCommand]
    public async Task ResetToDefaultAsync()
    {
        await _config.ResetAsync();
        LoadFromConfig();
        OnNotice("已恢复默认设置。Dock 与文件夹引用列表已清空。");
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            _autoStart.SetEnabled(value);
            _config.Current.AutoStart = value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置开机自启失败");
            OnNotice($"设置开机自启失败：{ex.Message}");
        }
    }

    partial void OnHotkeyTextChanged(string value) => Apply(() => _config.Current.Hotkey = value.Trim());

    partial void OnLauncherHotkeyTextChanged(string value) => Apply(() => _config.Current.LauncherHotkey = value.Trim());

    partial void OnFolderHotkeyTextChanged(string value) => Apply(() => _config.Current.FolderHotkey = value.Trim());

    partial void OnThemeOptionChanged(string value) => Apply(() => _config.Current.Theme = value);

    partial void OnDockPositionChanged(string value) => Apply(() => _config.Current.DockPosition = value);

    partial void OnDockIconSizeChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        _config.Current.DockIconSize = Math.Clamp(value, AppConfig.DockIconSizeMin, AppConfig.DockIconSizeMax);
        SaveDebounced();
    }

    partial void OnDockOpacityPercentChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        _config.Current.DockOpacity = Math.Clamp(value, 0, 100) / 100.0;
        SaveDebounced();
    }

    partial void OnBlurEnabledChanged(bool value) => Apply(() => _config.Current.BlurEnabled = value);

    partial void OnAutoHideDockChanged(bool value) => Apply(() => _config.Current.AutoHideDock = value);

    partial void OnDesktopLayerModeChanged(bool value) => Apply(() => _config.Current.DesktopLayerMode = value);

    partial void OnDockFlushToEdgeChanged(bool value) => Apply(() => _config.Current.DockFlushToEdge = value);

    partial void OnTaskbarCoverEnabledChanged(bool value) => Apply(() => _config.Current.TaskbarCoverEnabled = value);

    partial void OnTaskbarCoverWidthChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        _config.Current.TaskbarCoverWidth = Math.Clamp(value, AppConfig.TaskbarCoverWidthMin, AppConfig.TaskbarCoverWidthMax);
        SaveDebounced();
    }

    private void Apply(Action write)
    {
        if (_loading)
        {
            return;
        }

        write();
        SaveNow();
    }

    private void SelectPage(bool general, bool appearance, bool data)
    {
        IsGeneralPage = general;
        IsAppearancePage = appearance;
        IsDataPage = data;
    }

    private void SaveNow()
    {
        _sliderDebounce.Stop();
        _ = SaveSafelyAsync();
    }

    private void SaveDebounced()
    {
        // 滑条拖动时每像素都落盘会卡顿，合并成一次写入。
        _sliderDebounce.Stop();
        _sliderDebounce.Start();
    }

    private async void OnSliderDebounceTick(object? sender, EventArgs e)
    {
        _sliderDebounce.Stop();
        await SaveSafelyAsync();
    }

    private Task FlushAsync()
    {
        _sliderDebounce.Stop();
        return _config.SaveAsync();
    }

    private async Task SaveSafelyAsync()
    {
        try
        {
            await _config.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置失败");
            OnNotice($"保存配置失败：{ex.Message}");
        }
    }

    private static void CopyInto(AppConfig target, AppConfig source)
    {
        target.AutoStart = source.AutoStart;
        target.Hotkey = source.Hotkey;
        target.LauncherHotkey = source.LauncherHotkey;
        target.FolderHotkey = source.FolderHotkey;
        target.Theme = source.Theme;
        target.DockPosition = source.DockPosition;
        target.DockIconSize = source.DockIconSize;
        target.DockOpacity = source.DockOpacity;
        target.BlurEnabled = source.BlurEnabled;
        target.AutoHideDock = source.AutoHideDock;
        target.DesktopLayerMode = source.DesktopLayerMode;
        target.DockFlushToEdge = source.DockFlushToEdge;
        target.TaskbarCoverEnabled = source.TaskbarCoverEnabled;
        target.TaskbarCoverWidth = source.TaskbarCoverWidth;
        target.DesktopScanPrompted = source.DesktopScanPrompted;
        target.LaunchpadFolder = source.LaunchpadFolder;
        target.HiddenApps = source.HiddenApps;
        target.DockItems = source.DockItems;
        target.Folders = source.Folders;
        target.Groups = source.Groups;
    }

    private void OnNotice(string message, ToastKind kind = ToastKind.Error)
        => UserNotice?.Invoke(this, new NoticeEventArgs(message, kind));
}
