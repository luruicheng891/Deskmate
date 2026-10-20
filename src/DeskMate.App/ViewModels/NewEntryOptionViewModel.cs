using CommunityToolkit.Mvvm.ComponentModel;
using DeskMate.Core.Services;

namespace DeskMate.App.ViewModels;

/// <summary>
/// 新建浮层上的一个类型按钮。包一层是因为 WPF 的 RadioButton 需要一个能双向写的布尔，
/// 而 Core 里的 <c>NewEntryOption</c> 是条纯数据记录，不该为了界面加可观察状态。
/// </summary>
public sealed partial class NewEntryOptionViewModel : ObservableObject
{
    private readonly FolderManagerViewModel _owner;

    /// <summary>构造一个类型选项。</summary>
    public NewEntryOptionViewModel(FolderManagerViewModel owner, NewEntryOption option)
    {
        _owner = owner;
        Option = option;
    }

    /// <summary>这一项对应的类型。</summary>
    public NewEntryOption Option { get; }

    /// <summary>按钮上写的字。</summary>
    public string Label => Option.Label;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _owner.SelectCreateEntryType(Option);
        }
    }
}
