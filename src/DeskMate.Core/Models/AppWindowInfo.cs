namespace DeskMate.Core.Models;

/// <summary>某个应用当前打开的一个可切换窗口。</summary>
/// <param name="Handle">窗口句柄。</param>
/// <param name="ProcessId">所属进程。</param>
/// <param name="Title">窗口标题，预览卡片上显示。</param>
public readonly record struct AppWindowInfo(nint Handle, uint ProcessId, string Title);
