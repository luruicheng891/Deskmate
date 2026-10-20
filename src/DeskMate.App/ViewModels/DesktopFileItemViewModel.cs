using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMate.Core.Models;

namespace DeskMate.App.ViewModels;

/// <summary>
/// 桌面分类里的一个文件。只显示信息 + 提供「打开 / 在资源管理器中显示」，
/// 任何操作都不会移动或改名桌面上的文件。
/// </summary>
public sealed partial class DesktopFileItemViewModel : ObservableObject
{
    private readonly FolderManagerViewModel _owner;
    private readonly DesktopFile _file;

    /// <summary>构造条目。</summary>
    public DesktopFileItemViewModel(FolderManagerViewModel owner, DesktopFile file)
    {
        _owner = owner;
        _file = file;
        Path = file.Path;
    }

    /// <summary>完整路径（也用作去重键）。</summary>
    public string Path { get; }

    /// <summary>文件名，含扩展名。</summary>
    public string Name => _file.Name;

    /// <summary>卡片上的扩展名角标。没扩展名时用「文件」。</summary>
    public string Badge => _file.Extension.Length > 0 ? _file.Extension.ToUpperInvariant() : "文件";

    /// <summary>副标题：完整所在目录，一眼能看出是用户桌面还是公共桌面。</summary>
    public string Location => System.IO.Path.GetDirectoryName(_file.Path) ?? string.Empty;

    /// <summary>大小文本。</summary>
    public string SizeText => FileText.Size(_file.SizeBytes);

    /// <summary>修改时间文本。日期到「天」就够，时分在窄卡片上会被裁掉。</summary>
    public string ModifiedText => FileText.Modified(_file.ModifiedAt);

    /// <summary>是否已收藏。桌面文件与搜索结果里的文件卡片都能直接收藏，不必先变成受管文件夹。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarGlyph))]
    [NotifyPropertyChangedFor(nameof(StarTip))]
    private bool _isFavorite;

    /// <summary>右下角星星的字形：实心=已收藏，描边=还没收藏。</summary>
    public string StarGlyph => IsFavorite ? "\uE735" : "\uE734";

    /// <summary>星星的悬停说明。</summary>
    public string StarTip => IsFavorite
        ? "已收藏 · 点一下从「★ 收藏」里移出"
        : "收藏到「★ 收藏」页 · 只记路径，不动文件";

    /// <summary>点星星：收藏 / 取消收藏这个文件。</summary>
    [RelayCommand]
    public Task ToggleFavoriteAsync() => _owner.ToggleFavoriteAsync(Path, Name);

    /// <summary>双击：用系统关联程序打开，和桌面上双击一致。</summary>
    [RelayCommand]
    public void Open() => _owner.OpenDesktopFile(this);

    /// <summary>右键：在资源管理器中选中该文件。</summary>
    [RelayCommand]
    public void Reveal() => _owner.RevealDesktopFile(this);
}
