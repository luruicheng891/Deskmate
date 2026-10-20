namespace DeskMate.Core.Models;

/// <summary>
/// 一个正在运行、且有主窗口的应用。Dock 的运行区用它作为数据源。
/// </summary>
/// <param name="ExecutablePath">规范化的真实 exe 路径（长路径、大写盘符原样），同时用作去重键。</param>
/// <param name="DisplayName">显示名，取文件版本信息里的描述，取不到就用进程名。</param>
/// <param name="StartedAt">进程启动时间，用于稳定排序；受保护进程可能读不到。</param>
public sealed record RunningApp(string ExecutablePath, string DisplayName, DateTime? StartedAt);
