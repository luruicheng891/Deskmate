using DeskMate.App.ViewModels;
using DeskMate.App.Views;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.Services;

/// <summary>
/// 窗口协调器。DeskMate 的窗口全部是单例，由这里统一呼出与收起，
/// 并把「用户手动开关」与「全屏应用让路」两个独立状态合成 Dock 的最终可见性。
/// </summary>
public sealed class AppShell
{
    private readonly IServiceProvider _services;
    private readonly IConfigService _config;
    private readonly IHotkeyService _hotkeys;
    private readonly FullscreenService _fullscreen;
    private readonly ThemeService _theme;
    private readonly ToastService _toast;
    private readonly ILogger<AppShell> _logger;
    private bool _userWantsDock = true;
    private bool _dockWired;
    private bool _noticesWired;
    private string _hotkeySignature = string.Empty;

    /// <summary>构造协调器。</summary>
    public AppShell(
        IServiceProvider services,
        IConfigService config,
        IHotkeyService hotkeys,
        FullscreenService fullscreen,
        ThemeService theme,
        ToastService toast,
        ILogger<AppShell> logger)
    {
        _services = services;
        _config = config;
        _hotkeys = hotkeys;
        _fullscreen = fullscreen;
        _theme = theme;
        _toast = toast;
        _logger = logger;
    }

    /// <summary>Dock 是否被用户允许显示，托盘菜单文案依赖它。</summary>
    public event EventHandler? DockStateChanged;

    /// <summary>Dock 当前是否处于「用户允许显示」状态，托盘菜单勾选用。</summary>
    public bool IsDockEnabledByUser => _userWantsDock;

    /// <summary>在托盘窗口就绪（有消息泵）之后调用。</summary>
    public void Initialize()
    {
        _fullscreen.FullscreenChanged += OnFullscreenChanged;
        _fullscreen.StartWatching();
        _config.ConfigChanged += OnConfigChanged;
        _theme.ApplyCurrent();

        WireNotices();
        ShowDock();
        SyncHotkeys();
        _logger.LogInformation("AppShell 初始化完成");

        // 启动台首开要先扫应用再逐个取图标，等用户按下快捷键才做就必然看到一排空格子。
        // 推到后台预热：扫描与取图都排在 STA 线程上，不占 UI 线程，也不拖 Dock 的启动。
        _ = WarmLauncherAsync();
    }

    /// <summary>
    /// 把各视图模型的 <c>UserNotice</c> 统一接到非模态浮层。
    /// <para>
    /// 以前是五个窗口各自把同一个事件接到 <c>MessageBox.Show</c>：一个常驻桌面工具每做一步都要
    /// 用户点一次确定，而且 Dock 是不抢焦点的置顶窗口，模态框落在别的程序后面，观感就是「点了没反应」。
    /// 集中在协调器里还有一个好处：加新窗口不用记得再挂一次处理器。
    /// </para>
    /// </summary>
    private void WireNotices()
    {
        if (_noticesWired)
        {
            return;
        }

        _noticesWired = true;

        _services.GetRequiredService<DockViewModel>().UserNotice += OnNotice;
        _services.GetRequiredService<LauncherViewModel>().UserNotice += OnNotice;
        _services.GetRequiredService<FolderManagerViewModel>().UserNotice += OnNotice;
        _services.GetRequiredService<SearchViewModel>().UserNotice += OnNotice;
        _services.GetRequiredService<SettingsViewModel>().UserNotice += OnNotice;
        _services.GetRequiredService<TrayViewModel>().UserNotice += OnNotice;

        // 搜索框零结果时的「重新扫描应用」必须走启动台那条路：只刷新搜索自己的缓存，
        // 启动台还拿着旧列表，用户换个入口又找不到，等于没修好。
        _services.GetRequiredService<SearchViewModel>().RescanRequested += (_, _) => _ = RescanAppsAsync();
    }

    private void OnNotice(object? sender, NoticeEventArgs args)
        => _toast.Show(args.Message, args.Kind, args.ActionLabel, args.Action);

    /// <summary>后台把启动台的应用列表与图标先备好，首开直接是满的。</summary>
    private async Task WarmLauncherAsync()
    {
        try
        {
            await _services.GetRequiredService<LauncherViewModel>().LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "启动台预热失败，首开时再加载");
        }
    }

    /// <summary>Alt+Space 呼出 / 收起全局搜索。</summary>
    public void ToggleSearch() => _services.GetRequiredService<SearchWindow>().Toggle();

    /// <summary>托盘「显示/隐藏 Dock」。</summary>
    public void ToggleDock()
    {
        _userWantsDock = !_userWantsDock;
        ApplyDockState();
        DockStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>呼出启动台（单例窗口，第二次打开只是重新显示，所以能秒开）。</summary>
    public void ShowLauncher() => _services.GetRequiredService<LauncherWindow>().ShowLauncher();

    /// <summary>打开文件夹管理面板。</summary>
    public void ShowFolderManager() => _services.GetRequiredService<FolderManagerWindow>().ShowManager();

    /// <summary>快捷键用：开着且在前台就收回去，开着但在后面就提到前面，没开就呼出。</summary>
    public void ToggleLauncher()
    {
        var launcher = _services.GetRequiredService<LauncherWindow>();

        if (!launcher.IsVisible)
        {
            launcher.ShowLauncher();
            return;
        }

        if (launcher.IsActive)
        {
            launcher.HideLauncher();
            return;
        }

        launcher.Activate();
    }

    /// <summary>文件夹面板的同一套切换语义。</summary>
    public void ToggleFolderManager()
    {
        var manager = _services.GetRequiredService<FolderManagerWindow>();

        if (!manager.IsVisible)
        {
            manager.ShowManager();
            return;
        }

        if (manager.IsActive)
        {
            manager.Close();
            return;
        }

        manager.Activate();
    }

    /// <summary>打开设置窗口。</summary>
    public void ShowSettings() => _services.GetRequiredService<SettingsWindow>().ShowSettings();

    /// <summary>重新扫描应用列表，让启动台与全局搜索跟上刚装/刚卸的软件。</summary>
    public Task RescanAppsAsync() => _services.GetRequiredService<LauncherViewModel>().RefreshAsync(report: true);

    /// <summary>
    /// 欢迎窗：三条上手提示 + 「把桌面上的文件夹加入管理」。
    /// </summary>
    /// <param name="auto">true 表示首次启动自动弹，弹过一次就把 DesktopScanPrompted 记下来，不再 nag。</param>
    public async Task ShowWelcomeAsync(bool auto)
    {
        if (auto && _config.Current.DesktopScanPrompted)
        {
            return;
        }

        var folders = _services.GetRequiredService<IFolderService>();
        var desktopFolders = await folders.ScanDesktopFoldersAsync();

        // 已经管着的不再重复列出来，不然第二次打开还是那批「加入管理」。
        var known = new HashSet<string>(
            folders.GetAll().Select(item => item.RealPath),
            StringComparer.OrdinalIgnoreCase);
        var fresh = desktopFolders.Where(path => !known.Contains(path)).ToList();

        if (auto)
        {
            _config.Current.DesktopScanPrompted = true;
            await _config.SaveAsync();
        }

        var window = new Views.WelcomeWindow(fresh);
        if (window.ShowDialog() != true || window.FoldersToImport.Count == 0)
        {
            return;
        }

        await _services.GetRequiredService<FolderManagerViewModel>().AddPathsAsync(window.FoldersToImport);
        _logger.LogInformation("已从桌面导入 {Count} 个文件夹引用", window.FoldersToImport.Count);
    }

    /// <summary>显示 Dock 窗口并应用当前可见性状态。</summary>
    public void ShowDock()
    {
        var dock = _services.GetRequiredService<DockWindow>();

        // Dock 上的按钮只发出「要打开什么」的意图，具体呼出由协调器负责，避免 Dock 反向依赖其它窗口。
        if (!_dockWired)
        {
            dock.LauncherRequested += (_, _) => ShowLauncher();
            dock.FolderManagerRequested += (_, _) => ShowFolderManager();
            _dockWired = true;
        }

        if (!dock.IsVisible)
        {
            dock.Show();
        }

        ApplyDockState();
    }

    /// <summary>退出前收起所有窗口并解绑事件。</summary>
    public void Shutdown()
    {
        _fullscreen.FullscreenChanged -= OnFullscreenChanged;
        _config.ConfigChanged -= OnConfigChanged;
        _fullscreen.Dispose();
        _hotkeys.Unregister();

        if (_services.GetService<DockWindow>() is { } dock)
        {
            dock.CloseFromShell();
        }

        if (_services.GetService<TaskbarCoverWindow>() is { } cover)
        {
            cover.CloseFromShell();
        }

        // 平时点 X 只是隐藏，退出时必须真关，否则隐藏窗口还占着资源。
        if (_services.GetService<FolderManagerWindow>() is { } folders)
        {
            folders.CloseFromShell();
        }

        if (_services.GetService<SettingsWindow>() is { } settings)
        {
            settings.CloseFromShell();
        }

        if (_services.GetService<LauncherWindow>() is { } launcher)
        {
            launcher.Close();
        }

        if (_services.GetService<SearchWindow>() is { } search)
        {
            search.Close();
        }
    }

    private void OnFullscreenChanged(object? sender, bool isFullscreen)
    {
        // 全屏让路只做「隐藏」，恢复时仍要尊重用户自己的开关，所以在这里重算而不是直接 Show。
        _logger.LogDebug("全屏状态变化：{Fullscreen}", isFullscreen);
        ApplyDockState();
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _theme.ApplyCurrent();

        // 设置页改了任何一组快捷键都要重新注册，否则新按键组合不生效。
        SyncHotkeys();

        // 遮罩的开关与宽度都只写在这里生效：Dock 自己会响应 ConfigChanged，不需要重算一遍可见性。
        if (_services.GetService<TaskbarCoverWindow>() is { } cover)
        {
            cover.SetVisibility(_config.Current.TaskbarCoverEnabled && !_fullscreen.IsForegroundFullscreen());
        }
    }

    /// <summary>
    /// 按配置同步三个入口的全局快捷键（搜索 / 启动台 / 文件夹面板）。
    /// <para>
    /// 配置每次改动都会广播（拖滑条时也广播），而注销再注册之间有一个按键可能落空的窗口，
    /// 所以先比签名，只有快捷键这一组真的变了才重注册。
    /// </para>
    /// </summary>
    private void SyncHotkeys()
    {
        var bindings = HotkeyBindings().ToList();
        var signature = string.Join('|', bindings.Select(binding => binding.Text));
        if (signature == _hotkeySignature)
        {
            return;
        }

        _hotkeySignature = signature;
        RegisterHotkeys(bindings);
    }

    /// <summary>每个入口一组：显示名、组合键文本（空 = 不绑定）、命中动作。</summary>
    private IEnumerable<(string Name, string Text, Action Action)> HotkeyBindings()
    {
        var current = _config.Current;

        yield return ("搜索", current.Hotkey, ToggleSearch);
        yield return ("启动台", current.LauncherHotkey, ToggleLauncher);
        yield return ("文件夹面板", current.FolderHotkey, ToggleFolderManager);
    }

    private void RegisterHotkeys(IReadOnlyList<(string Name, string Text, Action Action)> bindings)
    {
        _hotkeys.Unregister();

        var failures = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, text, action) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!taken.Add(text))
            {
                failures.Add($"「{name}」的 {text} 与另一个入口撞了");
                continue;
            }

            if (!_hotkeys.Register(text, action))
            {
                failures.Add($"「{name}」的 {text} 注册失败：{_hotkeys.LastError ?? "未知原因"}");
            }
        }

        if (failures.Count == 0)
        {
            return;
        }

        var summary = string.Join("；", failures);
        _logger.LogWarning("全局快捷键注册失败：{Failures}", summary);

        // 搜索只有 Alt+Space 一个入口，它注册不上等于功能不可用，必须当场把话说清楚并指路。
        if (summary.Contains("「搜索」", StringComparison.Ordinal))
        {
            System.Windows.MessageBox.Show(
                $"{summary}\n\n可在「设置 - 通用」里换一个组合键；换之前也能从托盘图标右键菜单打开搜索。",
                "DeskMate",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        _toast.Warning(summary + "。可在「设置 - 通用」里换一个组合。");
    }

    private void ApplyDockState()
    {
        if (_services.GetService<DockWindow>() is not { } dock)
        {
            return;
        }

        var hiddenByFullscreen = _fullscreen.IsForegroundFullscreen();
        var visible = _userWantsDock && !hiddenByFullscreen;
        dock.SetVisibility(visible);

        // 遮罩是独立开关，但同样要在全屏时让路；它自己会判断有没有东西可盖。
        if (_services.GetService<TaskbarCoverWindow>() is { } cover)
        {
            cover.SetVisibility(_config.Current.TaskbarCoverEnabled && !hiddenByFullscreen);
        }
    }
}
