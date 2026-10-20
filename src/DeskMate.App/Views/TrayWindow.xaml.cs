using System.Windows;
using System.Windows.Interop;
using DeskMate.App.ViewModels;
using DeskMate.Core.Services;
using DeskMate.Interop;

namespace DeskMate.App.Views;

/// <summary>
/// 隐藏宿主窗口：托盘图标 + 快捷键消息接收端。
/// </summary>
public partial class TrayWindow : Window
{
    private readonly HotkeyService _hotkeys;

    /// <summary>构造托盘宿主窗口。</summary>
    public TrayWindow(TrayViewModel viewModel, HotkeyService hotkeys)
    {
        InitializeComponent();

        _hotkeys = hotkeys;
        DataContext = viewModel;

        // ContextMenu 里的绑定靠 TaskbarIcon 的 DataContext 解析，显式设一次更稳。
        TrayIcon.DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        // 有 HWND 之后才能挂 WM_HOTKEY 钩子。
        var source = HwndSource.FromHwnd(hwnd)
            ?? throw new InvalidOperationException("托盘窗口未能获得 HwndSource，全局快捷键无法工作。");
        _hotkeys.AttachSource(source);

        WindowInterop.ApplyToolWindowStyle(hwnd);
    }
}
