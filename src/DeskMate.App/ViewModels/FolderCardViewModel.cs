using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DeskMate.App.ViewModels;

/// <summary>分组 Tab（或右键菜单里的分组项）。</summary>
/// <param name="Id">"all" / "none" / 分组 Id。</param>
/// <param name="Name">显示名。</param>
public record GroupTab(string Id, string Name);

/// <summary>
/// 面包屑上的一段。
/// </summary>
/// <param name="Display">显示用的名字（盘符那一段就是「E:\」）。</param>
/// <param name="Path">这一段的完整路径，点它就跳到这一层。</param>
/// <param name="IsLast">是不是当前这一层：最后一段后面不该再拖一个「›」。</param>
public sealed record BreadcrumbItem(string Display, string Path, bool IsLast);

/// <summary>
/// 文件夹预览里的一行。做成可观察类而不是 record：这一行也能收藏，
/// 点星星要求状态立刻在界面上翻过来，record 通知不了视图。
/// </summary>
public sealed partial class PreviewEntry : ObservableObject
{
    private readonly FolderManagerViewModel _owner;

    /// <summary>构造预览行。<paramref name="path"/> 为空的占位行（「仅显示前 50 项」）不给收藏按钮。</summary>
    public PreviewEntry(FolderManagerViewModel owner, string name, string path, bool isDirectory)
    {
        _owner = owner;
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
    }

    /// <summary>条目名。</summary>
    public string Name { get; }

    /// <summary>完整路径；占位行则为空。</summary>
    public string Path { get; }

    /// <summary>是否为目录。</summary>
    public bool IsDirectory { get; }

    /// <summary>这一行是不是真路径（占位提示行不是）。</summary>
    public bool HasPath => !string.IsNullOrEmpty(Path);

    /// <summary>是不是正在右边看内容的那一项（列表与内容同时出现时，得看得出选中了谁）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>大小文本；目录不显示大小（要现数一遍不值得）。</summary>
    [ObservableProperty]
    private string _sizeText = string.Empty;

    /// <summary>修改日期文本。</summary>
    [ObservableProperty]
    private string _modifiedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarGlyph))]
    [NotifyPropertyChangedFor(nameof(StarTip))]
    private bool _isFavorite;

    /// <summary>星星字形：实心=已收藏。</summary>
    public string StarGlyph => IsFavorite ? "\uE735" : "\uE734";

    /// <summary>星星提示。</summary>
    public string StarTip => IsFavorite
        ? "已收藏 · 点一下从「★ 收藏」里移出"
        : "收藏到「★ 收藏」页 · 只记路径，不动文件";

    [RelayCommand]
    public Task ToggleFavoriteAsync() => _owner.ToggleFavoriteAsync(Path, Name);

    /// <summary>右键「打开」：与双击同一件事，交给系统关联程序。</summary>
    [RelayCommand]
    public void Open() => _owner.OpenPreviewEntry(this);

    /// <summary>右键「在资源管理器中显示」：想跳出到系统里时用这一下。</summary>
    [RelayCommand]
    public void Reveal() => _owner.RevealPreviewEntry(this);

    /// <summary>右键「重命名」：改的是磁盘上那个名字，位置不动。输入框与校验由窗口给。</summary>
    [RelayCommand]
    public void Rename() => _owner.RequestEntryRename(this);

    /// <summary>右键「移到回收站」：窗口会先二次确认。</summary>
    [RelayCommand]
    public void Recycle() => _owner.RequestEntryRecycle(this);
}

/// <summary>
/// 顶部 Tab 的视图模型。单独可观察是因为 WPF 的 RadioButton 需要能把选中状态写回来，
/// 而 ConverterParameter 不能绑定，没法用「Id 相等」的写法做互斥高亮。
/// <para>
/// 改名与删除的命令挂在自己身上（不是靠 ContextMenu 往上找祖先）：
/// 右键菜单的 DataContext 就是这张 Tab，绑祖先会解析到 Tab 自己身上，命令取不到就是点了没反应。
/// </para>
/// </summary>
public sealed partial class GroupTabViewModel : ObservableObject
{
    private readonly FolderManagerViewModel _owner;

    /// <summary>构造 Tab。</summary>
    public GroupTabViewModel(FolderManagerViewModel owner, string id, string name)
    {
        _owner = owner;
        Id = id;
        Name = name;
    }

    /// <summary>"all" / "none" / 分组 Id / "desktop:image" 这类桌面分类。</summary>
    public string Id { get; }

    /// <summary>显示名（分组与桌面分类都带数量）。</summary>
    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>右键：改这个分组的名字。内置视图由视图模型挡下来并说明原因。</summary>
    [RelayCommand]
    public void Rename() => _owner.BeginRenameGroup(this);

    /// <summary>右键：请求删除这个分组（会先弹确认）。</summary>
    [RelayCommand]
    public void RequestDelete() => _owner.RequestDeleteGroup(this);
}

/// <summary>
/// 文件夹卡片视图模型。<see cref="RealPath"/> 只是引用，
/// 本类的任何命令都不会改动磁盘上的内容 —— 需求核心原则第一条。
/// </summary>
public sealed partial class FolderCardViewModel : ObservableObject
{
    private readonly FolderManagerViewModel _owner;

    /// <summary>构造卡片。</summary>
    public FolderCardViewModel(
        FolderManagerViewModel owner,
        string id,
        string displayName,
        string realPath,
        string? groupId,
        string? color,
        bool isValid)
    {
        _owner = owner;
        Id = id;
        RealPath = realPath;
        _displayName = displayName;
        _groupId = groupId;
        _color = color;
        _isValid = isValid;
    }

    /// <summary>配置里的稳定 Id。</summary>
    public string Id { get; }

    /// <summary>真实目录路径，只读引用。</summary>
    public string RealPath { get; }

    /// <summary>预设 8 色，右键菜单直接绑定。</summary>
    public IReadOnlyList<string> ColorOptions => FolderManagerViewModel.ColorPalette;

    /// <summary>「换分组」子菜单数据源。</summary>
    public ObservableCollection<GroupTab> GroupMenu => _owner.GroupMenu;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string? _groupId;

    [ObservableProperty]
    private string? _color;

    [ObservableProperty]
    private bool _isValid;

    /// <summary>是否被选中（Ctrl 加选、Shift 连选、Ctrl+A 全选）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>是否已收藏（右下角星星）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarGlyph))]
    [NotifyPropertyChangedFor(nameof(StarTip))]
    private bool _isFavorite;

    /// <summary>右下角星星的字形：实心=已收藏，描边=还没收藏。</summary>
    public string StarGlyph => IsFavorite ? "\uE735" : "\uE734";

    /// <summary>星星的悬停说明，第一次用的人靠它知道这是干什么的。</summary>
    public string StarTip => IsFavorite
        ? "已收藏 · 点一下从「★ 收藏」里移出"
        : "收藏到「★ 收藏」页 · 只记路径，不动文件";

    /// <summary>「＋ 新建」在这条引用失效时不可用：目录都不在了，没地方可建。</summary>
    public bool CanCreate => IsValid;

    partial void OnIsValidChanged(bool value) => OnPropertyChanged(nameof(CanCreate));

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private bool _isCountLoading = true;

    /// <summary>
    /// 数不出来（没权限、盘暂时不可达）。这一条必须和「空」分开：
    /// 一个目录被标成「空」，用户的解读往往是「我的文件没了」。
    /// </summary>
    [ObservableProperty]
    private bool _isCountFailed;

    /// <summary>计数未回来时显示「统计中…」，超过 10000 显示「10000+」。</summary>
    public string CountText => IsCountLoading
        ? "统计中…"
        : IsCountFailed
            ? "读不到"
            : FileCount switch
            {
                <= 0 => "空",
                >= 10000 => "10000+",
                _ => $"{FileCount} 个文件",
            };

    partial void OnFileCountChanged(int value)
    {
        IsCountLoading = false;
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnIsCountLoadingChanged(bool value) => OnPropertyChanged(nameof(CountText));

    partial void OnIsCountFailedChanged(bool value) => OnPropertyChanged(nameof(CountText));

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(InitialLetter));

    /// <summary>占位图标上的首字母。</summary>
    public string InitialLetter => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    /// <summary>在资源管理器中打开该文件夹 —— 由双击触发。</summary>
    [RelayCommand]
    public void Open() => _owner.OpenCard(this);

    /// <summary>预览内部条目前 50 项 —— 由单击触发。</summary>
    [RelayCommand]
    public void Preview() => _owner.PreviewCard(this);

    /// <summary>右键：重命名显示名。</summary>
    [RelayCommand]
    public void Rename() => _owner.BeginRename(this);

    /// <summary>右键：路径失效时改指到新的位置。</summary>
    [RelayCommand]
    public void Relocate() => _owner.BeginRelocate(this);

    /// <summary>点右下角星星或右键：收藏 / 取消收藏。</summary>
    [RelayCommand]
    public Task ToggleFavoriteAsync() => _owner.ToggleFavoriteAsync(this);

    /// <summary>点「＋」：在这个目录里新建文件夹或文件（浮层由窗口给）。</summary>
    [RelayCommand]
    public void Create() => _owner.BeginCreateEntry(this);

    /// <summary>右键：移除引用。</summary>
    [RelayCommand]
    public async Task RemoveAsync()
    {
        _owner.SetContextCard(this);
        await _owner.RemoveContextCardAsync();
    }

    /// <summary>右键：设置颜色标签，null 为清除。</summary>
    [RelayCommand]
    public async Task ApplyColorAsync(string? color)
    {
        _owner.SetContextCard(this);
        await _owner.ApplyColorAsync(color);
    }

    /// <summary>右键：换分组。</summary>
    [RelayCommand]
    public async Task ApplyGroupAsync(GroupTab? tab)
    {
        _owner.SetContextCard(this);
        await _owner.ApplyGroupAsync(tab);
    }
}
