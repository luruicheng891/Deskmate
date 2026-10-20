using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskMate.App.ViewModels;

/// <summary>
/// 「★ 收藏」页里的一张卡片。收藏的是路径引用，目录、文件、压缩包都收，
/// 打开走系统默认方式（目录=资源管理器，文件=关联程序），DeskMate 不碰文件本身。
/// </summary>
public sealed partial class FavoriteCardViewModel : ObservableObject
{
    private readonly FolderManagerViewModel _owner;

    /// <summary>构造收藏卡片。详情（是不是目录、多大、几个文件）由视图模型在后台填进来。</summary>
    public FavoriteCardViewModel(FolderManagerViewModel owner, string path, string title, bool isManaged)
    {
        _owner = owner;
        FullPath = path;
        Title = title;
        IsManaged = isManaged;
    }

    /// <summary>收藏的完整路径。</summary>
    public string FullPath { get; }

    /// <summary>显示名：受管目录用它自己的显示名，其余用文件名。</summary>
    public string Title { get; }

    /// <summary>这条收藏同时是一个已管理的文件夹（卡片上能看出条目数）。</summary>
    public bool IsManaged { get; }

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private bool _isValid = true;

    [ObservableProperty]
    private string _metaText = "读取中…";

    /// <summary>角标：目录用文件夹字形，文件用扩展名（ZIP / C / DOCX）。</summary>
    [ObservableProperty]
    private string _badge = string.Empty;

    /// <summary>所在目录，副标题用。</summary>
    public string Location => System.IO.Path.GetDirectoryName(
        FullPath.TrimEnd('\\', '/')) ?? string.Empty;

    /// <summary>收藏页上的星星永远是实心的，点一下就是取消收藏。</summary>
    public string StarGlyph => "\uE735";

    /// <summary>右键或单击页面上的「预览」：在右侧预览栏里看它（目录列内容，文件列它所在目录）。</summary>
    [RelayCommand]
    public void Preview() => _owner.PreviewFavorite(this);

    /// <summary>双击或右键「打开」：用系统默认方式打开。</summary>
    [RelayCommand]
    public void Open() => _owner.OpenFavorite(this);

    /// <summary>右键：在资源管理器中选中它。</summary>
    [RelayCommand]
    public void Reveal() => _owner.RevealFavorite(this);

    /// <summary>右键或点星星：取消收藏。只去掉配置里的一行，磁盘上什么都不动。</summary>
    [RelayCommand]
    public Task UnfavoriteAsync() => _owner.UnfavoriteAsync(this);
}
