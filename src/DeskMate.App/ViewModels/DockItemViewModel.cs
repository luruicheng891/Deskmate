using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskMate.App.ViewModels;

/// <summary>
/// Dock 上一个图标的视图模型。命令都委托回 <see cref="DockViewModel"/>，
/// 让「写配置」这件事只有一个出口。
/// </summary>
public sealed partial class DockItemViewModel : ObservableObject
{
    private readonly DockViewModel _owner;

    /// <summary>构造 Dock 项。</summary>
    public DockItemViewModel(DockViewModel owner, string id, string displayName, string targetPath, bool isPinned)
    {
        _owner = owner;
        Id = id;
        TargetPath = targetPath;
        _displayName = displayName;
        _isPinned = isPinned;
    }

    /// <summary>配置里的稳定 Id。</summary>
    public string Id { get; }

    /// <summary>启动目标（exe 或 lnk）。</summary>
    public string TargetPath { get; }

    /// <summary>解析后的真实 exe，用于运行状态判定与「打开文件位置」。</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>取图标实际用的路径：设了自定义图标就是那个文件，否则是启动目标。</summary>
    public string IconSource { get; set; } = "";

    /// <summary>有没有设过自定义图标。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconRestoreVisibility))]
    private bool _hasIconOverride;

    /// <summary>「恢复自动图标」只在真的自定义过时出现。</summary>
    public Visibility IconRestoreVisibility => HasIconOverride ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private BitmapSource? _icon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseVisibility))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>目标还在不在。软件被卸载或换目录后，图标不能还是一副「一切正常」的样子。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InvalidVisibility))]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    private bool _isValid = true;

    /// <summary>
    /// 进程在跑、但一个「算任务」的窗口都没有 —— 缩回托盘的那类应用（微信、QQ）。
    /// 点它等于重新拉起，不标出来用户只会认为「我明明开着，怎么又开一个」。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    private bool _isHiddenInTray;

    /// <summary>失效标记的显示控制。</summary>
    public Visibility InvalidVisibility => IsValid ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>悬停文字：把「为什么点它会重新开一个」这种反直觉的行为说在前面。</summary>
    public string TooltipText => !IsValid
        ? $"{DisplayName} · 目标已不存在，右键可重新选择目标"
        : IsHiddenInTray
            ? $"{DisplayName} · 正在运行，但窗口缩回了托盘 · 点图标会重新打开它"
            : DisplayName;

    [ObservableProperty]
    private double _iconSize = 56;

    /// <summary>是否在这一项左边画分隔线：只有运行区与固定区之间那一项为 true。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeparatorVisibility))]
    private bool _showSeparator;

    /// <summary>给 XAML 用：分隔线要不要显示。</summary>
    public Visibility SeparatorVisibility => ShowSeparator ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>运行区的临时项不在配置里，「从 Dock 移除」对它没意义。</summary>
    public Visibility RemoveVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>只有还没固定的运行项才需要「固定到 Dock」。</summary>
    public Visibility PinVisibility => IsPinned ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>「关闭窗口」只对当下真的在跑的图标有意义（一个窗口都没有的话，那是一条点下去没反应的菜单）。</summary>
    public Visibility CloseVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsPinnedChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoveVisibility));
        OnPropertyChanged(nameof(PinVisibility));
    }

    /// <summary>左键点击图标：切回已开窗口，没开则启动。</summary>
    [RelayCommand]
    public async Task LaunchAsync() => await _owner.LaunchItemAsync(Id);

    /// <summary>右键：把运行中的临时图标钉成固定项。</summary>
    [RelayCommand]
    public async Task PinToDockAsync() => await _owner.PinItemAsync(Id);

    /// <summary>右键：从 Dock 移除。</summary>
    [RelayCommand]
    public async Task RemoveAsync() => await _owner.RemoveItemAsync(Id);

    /// <summary>右键：打开文件位置。</summary>
    [RelayCommand]
    public void OpenLocation() => _owner.OpenLocation(Id);

    /// <summary>右键：固定到最前。</summary>
    [RelayCommand]
    public async Task PinToFrontAsync() => await _owner.PinToFrontAsync(Id);

    /// <summary>右键「关闭窗口」：关掉这个应用当前所有窗口，等价于逐个点它们标题栏的 ×。</summary>
    [RelayCommand]
    public Task CloseAsync() => _owner.CloseAllWindowsAsync(Id);

    /// <summary>右键：目标不在了，重新挑一个。</summary>
    [RelayCommand]
    public void Relocate() => _owner.BeginRelocate(Id);

    /// <summary>右键：改这个图标在 Dock 上显示的名字（不影响磁盘上的文件名）。</summary>
    [RelayCommand]
    public void Rename() => _owner.BeginRename(Id);

    /// <summary>右键：换一个图标来源。</summary>
    [RelayCommand]
    public void ChangeIcon() => _owner.BeginChangeIcon(Id);

    /// <summary>右键：取消自定义图标，回到用目标自己的。</summary>
    [RelayCommand]
    public Task RestoreIconAsync() => _owner.RestoreIconAsync(Id);
}
