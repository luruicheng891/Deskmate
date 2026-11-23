using System.Diagnostics;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 窗口查找与切换。只读地观察系统窗口列表并调用前台切换，
/// 不会结束、修改或注入任何其它进程。
/// </summary>
public sealed class WindowService : IWindowService
{
    private readonly ILogger<WindowService> _logger;

    /// <summary>托盘捞回的窗口至少要有这么大才算「真主界面」；更小的都是助手窗/widget，捞出来只是白框。</summary>
    private const int MinRestorableWidth = 260;

    private const int MinRestorableHeight = 200;

    /// <summary>构造窗口服务。</summary>
    public WindowService(ILogger<WindowService> logger) => _logger = logger;

    /// <inheritdoc />
    public Task<IReadOnlyList<AppWindowInfo>> GetWindowsAsync(string executablePath)
        => Task.Run<IReadOnlyList<AppWindowInfo>>(() => Collect(
            string.IsNullOrWhiteSpace(executablePath)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(executablePath)));

    /// <inheritdoc />
    public Task<IReadOnlyList<AppWindowInfo>> GetWindowsByProcessNameAsync(string processName)
        => Task.Run<IReadOnlyList<AppWindowInfo>>(() => Collect(processName));

    /// <inheritdoc />
    public AppWindowInfo? PickClickTarget(IReadOnlyList<AppWindowInfo> windows, nint foregroundHandle)
    {
        if (windows.Count == 0)
        {
            return null;
        }

        // 前台那个优先：点已在前台的图标，语义应该是「收起来」而不是没反应。
        foreach (var window in windows)
        {
            if (window.Handle == foregroundHandle)
            {
                return window;
            }
        }

        // EnumWindows 本身就是自顶向下的 Z 序，第一个即最上面的窗口。
        return windows[0];
    }

    /// <inheritdoc />
    public async Task<bool> SwitchToAsync(string executablePath)
    {
        var foreground = WindowInterop.GetForeground();
        var name = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(executablePath);
        var target = PickClickTarget(await GetWindowsAsync(executablePath), foreground);

        if (target is null)
        {
            // 启动器壳进程兜底：有些 Dock 项对应的是启动器 exe，绿点亮的是壳（进程名匹配上了），
            // 真窗口却开在它派生的子进程里 —— 不兜底的话，用户每点一次就多开一个新实例。
            target = PickClickTarget(await GetWindowsOfDescendantsAsync(name), foreground);
        }

        if (target is null)
        {
            return false;
        }

        var action = WindowActivator.ToggleForeground(target.Value.Handle);

        // 系统偶尔会挡住前台切换（SetForegroundWindow 的返回值不可信，实现里已按实际句柄复核）。
        // 但这个应用确实有窗口，所以照样返回「已处理」—— 宁可切换没生效，也绝不能因此多开一个实例。
        _logger.LogDebug("切换到 {Title}：{Action}", target.Value.Title, action);

        return true;
    }

    /// <summary>
    /// 取某进程往下两代的可切换窗口。两代：不少启动器中间还隔一层引导进程（启动器 → 引导器 → 主程序）。
    /// 给「壳进程兜底」用 —— 壳自己通常一个窗口都没有。
    /// </summary>
    private async Task<IReadOnlyList<AppWindowInfo>> GetWindowsOfDescendantsAsync(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return [];
        }

        return await Task.Run<IReadOnlyList<AppWindowInfo>>(() =>
        {
            Process[] roots = [];

            try
            {
                roots = Process.GetProcessesByName(processName);
                if (roots.Length == 0)
                {
                    return [];
                }

                var parentByPid = new Dictionary<int, int>();
                Process[] all = [];

                try
                {
                    all = Process.GetProcesses();
                    foreach (var process in all)
                    {
                        if (ProcessTreeInterop.TryGetParentProcessId(process.Id, out var parentId))
                        {
                            parentByPid[process.Id] = parentId;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "枚举进程表失败，跳过壳进程兜底");
                    return [];
                }
                finally
                {
                    foreach (var process in all)
                    {
                        process.Dispose();
                    }
                }

                var descendants = CollectDescendants(
                    parentByPid, roots.Select(process => process.Id).ToList(), DescendantDepth);
                if (descendants.Count == 0)
                {
                    return [];
                }

                var windows = WindowDiscovery.GetWindowsOfProcesses(descendants.Select(id => (uint)id))
                    .Select(window => new AppWindowInfo(window.Handle, window.ProcessId, window.Title))
                    .ToList();

                if (windows.Count == 0)
                {
                    _logger.LogDebug(
                        "{Name} 的子进程 {Count} 个也没有可切换窗口（可能都缩在托盘）", processName, descendants.Count);
                }

                return windows;
            }
            finally
            {
                foreach (var process in roots)
                {
                    process.Dispose();
                }
            }
        });
    }

    /// <summary>壳进程往下收几代：一代覆盖绝大多数启动器，两代照顾中间还有引导器的。</summary>
    private const int DescendantDepth = 2;

    /// <summary>
    /// 按父进程号表往下收 <paramref name="depth"/> 代后代（<b>不含根</b>）。纯函数，便于单测。
    /// 有环的父子关系（理论上不该有，防御一下）会被「后代不重复收」挡住，不会死循环。
    /// </summary>
    public static IReadOnlyList<int> CollectDescendants(
        IReadOnlyDictionary<int, int> parentByPid, IReadOnlyCollection<int> rootIds, int depth)
    {
        var found = new List<int>();
        var frontier = new HashSet<int>(rootIds);

        for (var level = 0; level < depth; level++)
        {
            var next = new List<int>();
            foreach (var (processId, parentId) in parentByPid)
            {
                // 已在 frontier 里的不再收：既防环，也防「子进程被两个根共享」时重复。
                if (frontier.Contains(parentId) && !frontier.Contains(processId))
                {
                    next.Add(processId);
                }
            }

            found.AddRange(next);
            frontier.UnionWith(next);
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<bool> RestoreFromTrayAsync(string executablePath)
    {
        var processName = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(executablePath);
        if (processName.Length == 0)
        {
            return false;
        }

        // 产品名优先（exe 版本信息里的「微信」之类），进程名兜底 —— 标题里带它的才是真主窗口。
        var preferredTitle = GetProductName(executablePath);
        if (string.IsNullOrWhiteSpace(preferredTitle))
        {
            preferredTitle = processName;
        }

        return await Task.Run(() =>
        {
            Process[] processes = [];

            try
            {
                processes = Process.GetProcessesByName(processName);
                foreach (var process in processes)
                {
                    foreach (var candidate in CollectRestorable(process.Id, process.MainWindowHandle, preferredTitle))
                    {
                        // SW_RESTORE：主窗口多半被 hide() 或最小化了，按它自己上一次的状态恢复。
                        if (!WindowInterop.SetWindowRestored(candidate.Handle))
                        {
                            continue;
                        }

                        // ShowWindow 的返回值只说明「消息投出去了」，要真看得见才算捞回来。
                        if (!WindowInterop.IsVisible(candidate.Handle))
                        {
                            continue;
                        }

                        // 还要够大才算数：助手窗、widget 捞出来就是一块小白板，跳过它换下一个候选。
                        if (!WindowInterop.TryGetWindowRect(candidate.Handle, out var rect)
                            || rect.Width < MinRestorableWidth || rect.Height < MinRestorableHeight)
                        {
                            _logger.LogDebug(
                                "跳过 {Name} 的小窗 {Title}（{Width}×{Height}）：不像主界面",
                                processName, candidate.Title, rect.Width, rect.Height);
                            continue;
                        }

                        var action = WindowActivator.ToggleForeground(candidate.Handle);
                        _logger.LogDebug(
                            "已把 {Name} 的主窗口从托盘捞回：{Title}（{Action}）", processName, candidate.Title, action);
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        });
    }

    /// <summary>某进程的托盘捞回候选：窗口发现里「像主窗口」的，外加系统认的 MainWindowHandle（只补缺、不插队）。</summary>
    private static List<AppWindowInfo> CollectRestorable(int processId, nint mainWindowHandle, string preferredTitle)
    {
        var windows = WindowDiscovery.GetRestorableWindowsOfProcess((uint)processId).ToList();

        if (mainWindowHandle != IntPtr.Zero && windows.All(window => window.Handle != mainWindowHandle))
        {
            // 系统认的主窗口没进候选（比如那一瞬拿不到标题）也补进来，但只排在队尾：
            // 它的标题/面积都未知，没资格压过发现里排好序的候选。捞回动作自己会验证可见性。
            windows.Add(new WindowDiscovery.WindowInfo(mainWindowHandle, (uint)processId, string.Empty, default));
        }

        // 决胜顺序：标题含产品名/进程名的排最前 —— 真主窗口几乎都这么命名（微信主窗标题就是「微信」）；
        // 其余按面积从大到小。系统认的 MainWindowHandle 不许插队 —— 微信 4 实测它认到
        // 登录助手小窗上，直接捞就是一块白框。
        windows.Sort((a, b) => RankRestorable(b, preferredTitle).CompareTo(RankRestorable(a, preferredTitle)));

        return windows
            .Select(window => new AppWindowInfo(window.Handle, window.ProcessId, window.Title))
            .ToList();
    }

    /// <summary>捞回候选的决胜分：标题命中产品名/进程名优先，其次面积大者优先。</summary>
    private static (int Named, long Area) RankRestorable(WindowDiscovery.WindowInfo window, string preferredTitle)
    {
        var named = !string.IsNullOrWhiteSpace(preferredTitle)
                    && (window.Title ?? string.Empty).Contains(preferredTitle, StringComparison.OrdinalIgnoreCase);
        var area = (long)window.Bounds.Width * window.Bounds.Height;

        return (named ? 1 : 0, area);
    }

    /// <summary>读 exe 版本信息里的产品名（「微信」之类），读不到就返回空串。</summary>
    private static string GetProductName(string executablePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                var info = FileVersionInfo.GetVersionInfo(executablePath);
                if (!string.IsNullOrWhiteSpace(info.ProductName))
                {
                    return info.ProductName!;
                }
            }
        }
        catch
        {
            // 版本信息读不到（权限、路径变了）就用进程名兜底，不值得记日志。
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public bool Activate(AppWindowInfo window)
        => WindowActivator.ToggleForeground(window.Handle) != "nothing";

    /// <inheritdoc />
    public bool Close(AppWindowInfo window)
    {
        var closed = WindowActivator.Close(window.Handle);

        _logger.LogDebug("关闭窗口 {Title}：{Result}", window.Title, closed ? "已投出 WM_CLOSE" : "窗口已不在");

        return closed;
    }

    /// <inheritdoc />
    public async Task<int> CloseAllAsync(string executablePath)
    {
        var windows = await GetWindowsAsync(executablePath);
        var sent = 0;

        foreach (var window in windows)
        {
            if (WindowActivator.Close(window.Handle))
            {
                sent++;
            }
        }

        _logger.LogDebug("关闭 {Path} 的窗口：{Sent}/{Total}", executablePath, sent, windows.Count);

        return sent;
    }

    /// <inheritdoc />
    public async Task<int> KillAsync(string executablePath)
    {
        var processName = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(executablePath);
        var targetPath = NormalizePath(executablePath);

        return await Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return 0;
            }

            var killed = 0;
            Process[] processes = [];

            try
            {
                processes = Process.GetProcessesByName(processName);
                foreach (var process in processes)
                {
                    // 按名字圈定候选后，能读到 exe 路径的再核一遍路径：Dock 图标对应的必须是这个 exe，
                    // 不能把同名但来自别处的进程（比如用户自己开的另一份拷贝）误杀。
                    // 读不到路径（权限不够）时放弃杀它 —— 反正真要杀也多半杀不动。
                    if (targetPath.Length > 0)
                    {
                        try
                        {
                            var actual = NormalizePath(process.MainModule?.FileName);
                            if (actual.Length > 0 && !string.Equals(actual, targetPath, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogDebug("跳过 {Name}({Pid})：路径不匹配（{Actual}）", processName, process.Id, actual);
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "读 {Name}({Pid}) 的 exe 路径失败，跳过", processName, process.Id);
                            continue;
                        }
                    }

                    try
                    {
                        // 整棵树：很多应用带子进程（渲染进程、更新器），只杀主进程会留下孤儿。
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(3000);
                        killed++;
                        _logger.LogDebug("已结束进程 {Name}({Pid})", processName, process.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "结束进程 {Name}({Pid}) 失败", processName, process.Id);
                    }
                }

                return killed;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "枚举 {Name} 进程失败", processName);
                return killed;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        });
    }

    /// <summary>路径归一化：全小写 + 解析成完整路径，读不到就退回原样小写。</summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// 按进程名找出该进程当前的可切换窗口。<b>参数是不带扩展名的进程名，不是路径</b>
    /// —— .lnk 应已由调用方解析成真实 exe，这里只按名字比。
    /// </summary>
    private IReadOnlyList<AppWindowInfo> Collect(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return [];
        }

        var processIds = new List<uint>(4);
        Process[] processes = [];

        try
        {
            processes = Process.GetProcessesByName(processName);
            foreach (var process in processes)
            {
                processIds.Add((uint)process.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "枚举 {Name} 进程失败", processName);
            return [];
        }
        finally
        {
            // Process 实例各持一个内核句柄，不逐个释放会随点击次数累积。
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        if (processIds.Count == 0)
        {
            return [];
        }

        var windows = WindowDiscovery.GetWindowsOfProcesses(processIds)
            .Select(window => new AppWindowInfo(window.Handle, window.ProcessId, window.Title))
            .ToList();

        if (windows.Count == 0)
        {
            // 「找不到窗口」是这类问题唯一的现场证据：到底是没这个进程，还是它把自己缩回了托盘。
            // 后者不能从外面 ShowWindow 捞（会得到一个白框），只能交给启动流程。
            _logger.LogDebug("{Name} 有 {Count} 个进程，但没有可见窗口（大概是缩回托盘了）", processName, processIds.Count);
        }

        return windows;
    }
}
