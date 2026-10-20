using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMate.App.Services;
using DeskMate.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.ViewModels;

/// <summary>
/// 托盘菜单的视图模型。菜单项随模块推进逐个补齐（M5 会加满）。
/// </summary>
public sealed partial class TrayViewModel : ObservableObject
{
    private readonly AppShell _shell;
    private readonly IConfigService _config;
    private readonly IAutoStartService _autoStart;
    private readonly ILogger<TrayViewModel> _logger;

    /// <summary>构造托盘视图模型。</summary>
    public TrayViewModel(AppShell shell, IConfigService config, IAutoStartService autoStart, ILogger<TrayViewModel> logger)
    {
        _shell = shell;
        _config = config;
        _autoStart = autoStart;
        _logger = logger;

        _shell.DockStateChanged += (_, _) => RefreshState();
        _config.ConfigChanged += (_, _) => RefreshState();
        RefreshState();
    }

    [ObservableProperty]
    private string _dockMenuHeader = "隐藏 Dock";

    /// <summary>需要让用户知道的结果，由协调器统一交给非模态浮层。</summary>
    public event EventHandler<NoticeEventArgs>? UserNotice;

    [ObservableProperty]
    private bool _isAutoStartEnabled;

    /// <summary>搜索那一行的快捷键提示，跟着配置走，不能写死成 Alt+Space。</summary>
    [ObservableProperty]
    private string _searchHotkeyText = "Alt+Space";

    /// <summary>左键点托盘图标：切换 Dock。</summary>
    [RelayCommand]
    private void ToggleDock() => _shell.ToggleDock();

    /// <summary>
    /// 打开全局搜索。Alt+Space 有可能被中文输入法或别的程序占走，
    /// 而搜索以前只有那一个入口 —— 占用了就等于整个功能点不到，所以这里必须给一条鼠标路径。
    /// </summary>
    [RelayCommand]
    private void ShowSearch() => _shell.ToggleSearch();

    /// <summary>重新扫描应用：装完新软件不用重启 DeskMate。</summary>
    [RelayCommand]
    private Task RescanAppsAsync() => _shell.RescanAppsAsync();

    /// <summary>打开启动台。</summary>
    [RelayCommand]
    private void ShowLauncher() => _shell.ShowLauncher();

    /// <summary>打开文件夹管理面板。</summary>
    [RelayCommand]
    private void ShowFolderManager() => _shell.ShowFolderManager();

    /// <summary>打开设置。</summary>
    [RelayCommand]
    private void ShowSettings() => _shell.ShowSettings();

    /// <summary>切换开机自启（唯一写注册表的入口）。</summary>
    [RelayCommand]
    private async Task ToggleAutoStartAsync()
    {
        var target = !IsAutoStartEnabled;
        try
        {
            _autoStart.SetEnabled(target);
            _config.Current.AutoStart = target;
            await _config.SaveAsync();
            IsAutoStartEnabled = _autoStart.IsEnabled();
            UserNotice?.Invoke(this, new NoticeEventArgs(
                IsAutoStartEnabled ? "已开启开机自启（写入当前用户的启动项）" : "已关闭开机自启",
                IsAutoStartEnabled ? ToastKind.Success : ToastKind.Info));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换开机自启失败");
            UserNotice?.Invoke(this, new NoticeEventArgs($"设置开机自启失败：{ex.Message}"));
        }
    }

    /// <summary>重新打开首次启动那个欢迎窗，三条上手提示都在里面。</summary>
    [RelayCommand]
    private Task ShowWelcomeAsync() => _shell.ShowWelcomeAsync(auto: false);

    /// <summary>关于。</summary>
    [RelayCommand]
    private void ShowAbout()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0";
        MessageBox.Show(
            $"DeskMate v{version}\n\n桌面整理助手：Dock 快速启动、启动台、文件夹引用管理。\n" +
            $"数据目录：%APPDATA%\\DeskMate\n\nDeskMate 只引用文件夹路径，不会移动、改名或删除任何用户文件。",
            "关于 DeskMate",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>退出程序。</summary>
    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();

    private void RefreshState()
    {
        DockMenuHeader = _shell.IsDockEnabledByUser ? "隐藏 Dock" : "显示 Dock";
        IsAutoStartEnabled = _autoStart.IsEnabled();
        SearchHotkeyText = string.IsNullOrWhiteSpace(_config.Current.Hotkey) ? "未绑定" : _config.Current.Hotkey;
    }
}
