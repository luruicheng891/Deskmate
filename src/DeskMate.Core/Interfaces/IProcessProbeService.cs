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

    /// <summary>
    /// 盯住这些名字（不带扩展名）当前对应的进程，其中<strong>任意一个退出时回调一次</strong>；
    /// 回调在后台线程上，调用方要自己回到 UI 线程。
    /// <para>
    /// 为什么要有这一条：窗口关了不等于进程退了，而 Dock 的运行指示问的就是进程。只靠定时补刷的话，
    /// 「关掉应用、图标还在」这一眼就能看见的延迟怎么也得等一个补刷周期。进程退出是系统会主动通知的，
    /// 挂上去就能在它真的没了的那一刻立刻改图标。
    /// </para>
    /// <para>返回的对象要 Dispose：它替我们握着那些进程的内核句柄。整个过程只读，不碰别人的进程。</para>
    /// </summary>
    IDisposable WatchProcessExits(IEnumerable<string> processNames, Action onExit);
}

