using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskMate.App.ViewModels;

/// <summary>
/// 启动台里一个应用磁贴。
/// 命令一律挂在磁贴自己身上：右键菜单的 PlacementTarget 就是磁贴，
/// 若走 PlacementTarget.DataContext.X 去够启动台 VM，会因数据上下文是磁贴而静默解析失败。
/// </summary>
public sealed partial class AppTileViewModel : ObservableObject
{
    private readonly LauncherViewModel _owner;

    /// <summary>构造磁贴。</summary>
    public AppTileViewModel(
        LauncherViewModel owner,
        string id,
        string name,
        string path,
        string? iconPath,
        int usageCount,
        bool isHidden)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Path = path;
        IconPath = iconPath;
        UsageCount = usageCount;
        IsHidden = isHidden;
    }

    /// <summary>去重键。</summary>
    public string Id { get; }

    /// <summary>显示名。</summary>
    public string Name { get; }

    /// <summary>ShellExecute 的路径（通常是 .lnk）。</summary>
    public string Path { get; }

    /// <summary>图标来源路径。</summary>
    public string? IconPath { get; }

    /// <summary>历史启动次数，「常用」页排序用。</summary>
    public int UsageCount { get; }

    [ObservableProperty]
    private BitmapSource? _icon;

    /// <summary>是否被用户隐藏（决定右键菜单显示「隐藏」还是「恢复显示」）。</summary>
    public bool IsHidden { get; init; }

    /// <summary>没解析出磁贴归属时用于占位首字母。</summary>
    public string InitialLetter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    /// <summary>左键单击：启动应用。</summary>
    [RelayCommand]
    public void Open() => _owner.OpenTile(this);

    /// <summary>右键：添加到 Dock。</summary>
    [RelayCommand]
    public async Task AddToDockAsync() => await _owner.AddTileToDockAsync(this);

    /// <summary>右键：打开文件位置。</summary>
    [RelayCommand]
    public void OpenLocation() => _owner.OpenTileLocation(this);

    /// <summary>右键：从启动台隐藏。</summary>
    [RelayCommand]
    public async Task HideAsync() => await _owner.HideTileAsync(this);

    /// <summary>右键：把隐藏过的条目恢复显示。</summary>
    [RelayCommand]
    public async Task RestoreAsync() => await _owner.RestoreTileAsync(this);
}

/// <summary>
/// 一行磁贴。按行切分是为了让外层 VirtualizingStackPanel 能做真正的 UI 虚拟化：
/// WrapPanel 不参与虚拟化，几百个图标一次性生成实体会拖慢首开。
/// </summary>
public sealed class AppRowViewModel
{
    /// <summary>构造一行。</summary>
    public AppRowViewModel(IReadOnlyList<AppTileViewModel> tiles) => Tiles = tiles;

    /// <summary>本行磁贴。</summary>
    public IReadOnlyList<AppTileViewModel> Tiles { get; }
}
