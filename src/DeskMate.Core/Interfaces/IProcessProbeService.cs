using DeskMate.Core.Models;

namespace DeskMate.Core.Interfaces;

/// <summary>
/// 进程探活：Dock 图标下方的运行指示点、以及 Dock 的运行区都依赖它。
/// 一次快照供整排图标共用，避免每个图标各自全表扫描进程。
/// </summary>
public interface IProcessProbeService
{
    /// <summary>把 .lnk / .url 解析成真实 exe 路径；解析不出来时原样返回。</summary>
    Task<string> ResolveExecutablePathAsync(string path);

    /// <summary>取当前所有运行中进程的主模块名（小写、无扩展名）快照。</summary>
    Task<IReadOnlySet<string>> SnapshotRunningNamesAsync();

    /// <summary>依据快照判断某个 exe 是否在运行。</summary>
    bool IsRunning(string executablePath, IReadOnlySet<string> runningNames);

    /// <summary>
    /// 取「正在运行且有主窗口」的应用快照，按启动时间升序。
    /// 只认有主窗口的：缩回托盘的应用（微信那种）没有窗口，出现在 Dock 上也没法切回去。
    /// </summary>
    Task<IReadOnlyList<RunningApp>> SnapshotRunningAppsAsync();
}

