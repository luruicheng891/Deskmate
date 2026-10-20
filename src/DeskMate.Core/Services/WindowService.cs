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

    /// <summary>构造窗口服务。</summary>
    public WindowService(ILogger<WindowService> logger) => _logger = logger;

    /// <inheritdoc />
    public Task<IReadOnlyList<AppWindowInfo>> GetWindowsAsync(string executablePath)
        => Task.Run<IReadOnlyList<AppWindowInfo>>(() => Collect(executablePath));

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
        var windows = await GetWindowsAsync(executablePath);
        var target = PickClickTarget(windows, WindowInterop.GetForeground());

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

    private IReadOnlyList<AppWindowInfo> Collect(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return [];
        }

        // 进程名就是不带扩展名的 exe 文件名；.lnk 应已由调用方解析成真实 exe。
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        if (processName.Length == 0)
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
