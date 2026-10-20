using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskMate.App.ViewModels;
using Microsoft.Win32;

namespace DeskMate.App.Views;

/// <summary>
/// 设置窗口：通用 / 外观 / 数据三页。改动即时写回配置并广播刷新。
/// 快捷键捕获与文件对话框这类需要窗口参与的操作放在这里，视图模型只负责状态。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _closingForReal;

    /// <summary>构造设置窗口。</summary>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.ExportRequested += OnExportRequested;
        viewModel.ImportRequested += OnImportRequested;
        viewModel.PickLaunchpadFolderRequested += OnPickLaunchpadFolderRequested;
    }

    /// <summary>打开设置并同步界面值。</summary>
    public void ShowSettings()
    {
        _viewModel.LoadFromConfig();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    /// <summary>
    /// 单例窗口 Close 之后 WPF 不允许再 Show（会抛 InvalidOperationException），
    /// 所以点 X 只隐藏，退出程序时才真关。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closingForReal)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>退出时真正关闭窗口。</summary>
    public void CloseFromShell()
    {
        _closingForReal = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.ExportRequested -= OnExportRequested;
        _viewModel.ImportRequested -= OnImportRequested;
        _viewModel.PickLaunchpadFolderRequested -= OnPickLaunchpadFolderRequested;
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_viewModel.IsCapturingHotkey)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            _viewModel.EndHotkeyCapture();
            e.Handled = true;
            return;
        }

        var combo = BuildCombo(e);
        if (combo is not null)
        {
            _viewModel.ApplyCapturedHotkey(combo);
        }

        // 捕获期间吞掉所有按键，避免误触发系统或其他程序。
        e.Handled = true;
    }

    private void OnHotkeyBoxClick(object sender, MouseButtonEventArgs e)
    {
        // 三个框共用这一个处理器：Tag 里写着它是哪个入口的槽位。
        if (sender is not TextBox box)
        {
            return;
        }

        _viewModel.BeginHotkeyCapture(box.Tag as string ?? SettingsViewModel.SearchSlot);
        box.Focus();
        e.Handled = true;
    }

    private async void OnExportRequested(object? sender, EventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 DeskMate 配置",
            Filter = "JSON 配置|*.json",
            FileName = "deskmate-config.json",
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ExportToAsync(dialog.FileName);
        }
    }

    private async void OnImportRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 DeskMate 配置",
            Filter = "JSON 配置|*.json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // 导入是整套替换，选错一个 json 就把 Dock 和文件夹列表全换了，必须先问一句。
        var answer = MessageBox.Show(
            $"导入会用这个文件整个替换现在的配置：\n{dialog.FileName}\n\n" +
            $"当前有：{_viewModel.CurrentSummary}，这些都会被换掉。\n" +
            "磁盘上的文件不受影响，你现在的配置会先自动备份一份。\n\n确定导入吗？",
            "导入配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.ImportFromAsync(dialog.FileName);
        }
    }

    private void OnPickLaunchpadFolderRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择启动台的快捷方式文件夹（只能一个）",
            InitialDirectory = _viewModel.LaunchpadFolderDisplay,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.LaunchpadFolder = dialog.FolderName;
        }
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "重置会清空 Dock 与文件夹引用列表，恢复到出厂默认值。\n\n磁盘上的应用与文件夹不受影响。确定继续吗？",
            "重置为默认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.ResetToDefaultAsync();
        }
    }

    /// <summary>把按下的键翻译成像 "Ctrl+Alt+D" 这样的配置文本。</summary>
    private static string? BuildCombo(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 只按修饰键时不出结果，等用户补上真正的按键。
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Apps)
        {
            return null;
        }

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            return null;
        }

        var name = KeyName(key);
        if (name is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (mods.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (mods.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (mods.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(name);
        return string.Join("+", parts);
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.F1 and <= Key.F12 => $"F{key - Key.F1 + 1}",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        Key.Space => "Space",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.PrintScreen => "PrintScreen",
        _ => null,
    };

}
