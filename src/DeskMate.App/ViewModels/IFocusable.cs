namespace DeskMate.App.ViewModels;

/// <summary>
/// 面板里「可以用方向键走到」的一类条目。
/// 四个视图（卡片网格、浏览列表、收藏页、桌面分类）的数据类型各不相同，
/// 但键盘导航要共用同一套状态机，所以把「焦点、路径、是不是目录」收进这一个口子。
/// </summary>
public interface IFocusable
{
    /// <summary>键盘焦点是否落在这条上。鼠标点它也会落上来 —— 之后按回车就是它。</summary>
    bool IsFocused { get; set; }

    /// <summary>真实路径；占位提示行给空串（走到它上面什么也干不了）。</summary>
    string FocusPath { get; }

    /// <summary>是不是目录：回车时决定「走进去」还是「看内容」。</summary>
    bool FocusIsDirectory { get; }

    /// <summary>显示名，出错提示要说清是谁。</summary>
    string FocusName { get; }
}
