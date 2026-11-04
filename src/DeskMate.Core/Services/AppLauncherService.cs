using System.Diagnostics;
using DeskMate.Core.Interfaces;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 启动应用与打开资源管理器。
/// 一律走 ShellExecute，这样 .lnk / .url 与文件关联才生效（.lnk 里记录的参数也会自动带上）。
/// </summary>
public sealed class AppLauncherService : IAppLauncherService
{
    /// <summary>接手窗口最长等多久：Electron 那类应用（微信、编辑器、Steam）冷启动要好几秒才建出窗口。</summary>
    private const int RaiseBudgetMs = 8000;

    private const int RaiseIntervalMs = 150;

    /// <summary>shell 自己就能执行的扩展名：这些不需要问关联表，目标程序就是要找的程序本身。</summary>
    private static readonly HashSet<string> ShellExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".msu", ".lnk", ".msc", ".appref-ms", ".cpl", ".reg", ".url",
    };

    private readonly ILogger<AppLauncherService> _logger;
    private readonly IWindowService _windows;

    /// <summary>构造启动服务。</summary>
    public AppLauncherService(ILogger<AppLauncherService> logger, IWindowService windows)
    {
        _logger = logger;
        _windows = windows;
    }

    /// <inheritdoc />
    public bool HasDefaultProgram(string path)
    {
        var target = RequirePath(path);

        // 目录不需要关联程序，explorer 自己会开。
        if (Directory.Exists(target))
        {
            return true;
        }

        // 快捷方式与可执行文件本身就能被 shell 执行，拿 .lnk 去问关联表得到的是 null，
        // 于是「打开一个快捷方式」会被判成「这台机器上没有程序能打开它」。
        return IsShellExecutable(target) || ShellAssociation.ExecutableFor(Path.GetExtension(target)) is not null;
    }

    /// <inheritdoc />
    public void Launch(string path)
    {
        var target = RequirePath(path);
        var isDirectory = Directory.Exists(target);
        if (!isDirectory && !File.Exists(target))
        {
            throw new FileNotFoundException($"找不到要启动的项目：{target}", target);
        }

        // 只有「要靠别的程序接手」的文档才值得去查关联表。.lnk / .exe 拿去问，
        // 实测要么得到 null（.lnk）、要么得到 "%1" 这种占位串（.exe），都不能当接手程序用。
        var handler = isDirectory || IsShellExecutable(target)
            ? null
            : ShellAssociation.ExecutableFor(Path.GetExtension(target));

        Process? started = null;
        try
        {
            started = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            _logger.LogInformation("已启动 {Path}", target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动失败 {Path}", target);
            throw;
        }

        // 目录交给 explorer 自己开：它那一排窗口跟我们刚开的这个不是一回事，不能去抬。
        if (isDirectory)
        {
            started?.Dispose();
            return;
        }

        var processName = ProcessNameOf(started) ?? (handler is null ? null : Path.GetFileNameWithoutExtension(handler));
        if (processName is not null)
        {
            RaiseWindowSoon(processName);
        }
    }

    /// <summary>
    /// 启动之后把它的窗口抬到前台。
    /// <para>
    /// 为什么需要这一刀：Dock 自己的窗口是「点了不抢焦点」的（故意，为了不打断游戏与全屏），
    /// 所以从 Dock 起应用时，前台并不在我们手上，系统常把新窗口摆在后面。从桌面双击就没这问题
    /// —— 那是 explorer 起的，前台权直接交过去。用户读到的差别就是「Dock 点半天没打开，ALT+TAB 里也没它」。
    /// </para>
    /// <para>
    /// 单实例应用（微信、Typora）收到第二个实例转交的文件后只会更新自己那个已存在的窗口，新进程立刻退出；
    /// 按<b>进程名</b>找窗口正好覆盖这种情况：找到的就是那个老实例的窗口。
    /// </para>
    /// <para>
    /// 只做「还原 + 抬起」，绝不做任务栏那套 <c>ToggleForeground</c>：那个语义是「点已经在前台的图标就收起」，
    /// 借过来会变成「双击打开文件，结果文件窗口被最小化」。
    /// </para>
    /// </summary>
    private void RaiseWindowSoon(string processName)
    {
        // 记下「点下去的那一刻谁在前台」。之后前台换成了别的应用就说明用户已经去干别的了，
        // 这时候再把刚起的应用拽到前台是打扰，不是帮忙。
        var foregroundAtLaunch = WindowInterop.GetForeground();

        _ = Task.Run(async () =>
        {
            var deadline = Environment.TickCount64 + RaiseBudgetMs;
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(RaiseIntervalMs);

                var windows = await _windows.GetWindowsByProcessNameAsync(processName);
                var foreground = WindowInterop.GetForeground();

                if (windows.Count > 0)
                {
                    foreach (var window in windows)
                    {
                        if (window.Handle == foreground)
                        {
                            return; // 系统已经把它带到前面了，别乱动
                        }
                    }

                    if (foregroundAtLaunch != IntPtr.Zero && foreground != foregroundAtLaunch)
                    {
                        _logger.LogDebug("启动 {Name} 后用户已经切到别的窗口，不再抬它", processName);
                        return;
                    }

                    foreach (var window in windows)
                    {
                        if (WindowActivator.Activate(window.Handle))
                        {
                            _logger.LogDebug("已把新起的 {Name} 抬到前台", processName);
                            return;
                        }
                    }

                    return;
                }

                if (foregroundAtLaunch != IntPtr.Zero && foreground != foregroundAtLaunch)
                {
                    _logger.LogDebug("等 {Name} 的窗口期间前台换了别的应用，不再追它", processName);
                    return;
                }
            }

            _logger.LogDebug("启动 {Name} 后没等到它的窗口（可能本来就没有窗口）", processName);
        });
    }

    /// <summary>
    /// 刚起的那个进程叫什么名字。<b>要的是镜像路径里的名字，不是 <c>Process.ProcessName</c></b>：
    /// 从 .lnk 起的时候后者是快捷方式自己的文件名（「微信」），而真正该找的是目标进程的名字（「Weixin」）。
    /// shell 没交回进程句柄时（商店应用激活、DDE 转交）返回 null，退回按关联表里那个程序找。
    /// </summary>
    private static string? ProcessNameOf(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            var fromImage = PathInterop.TryGetProcessImagePath((uint)process.Id);
            if (!string.IsNullOrWhiteSpace(fromImage))
            {
                return Path.GetFileNameWithoutExtension(fromImage);
            }

            return string.IsNullOrWhiteSpace(process.ProcessName) ? null : process.ProcessName;
        }
        catch (Exception)
        {
            // 进程可能已经退了（单实例转交就是这么快）；拿不到名字就不抬窗口，别把启动本身带崩。
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsShellExecutable(string path) => ShellExecutableExtensions.Contains(Path.GetExtension(path));


    /// <inheritdoc />
    public void OpenInExplorer(string path)
    {
        var target = RequirePath(path);

        // explorer 的参数格式很挑：打开目录直接传路径，选中文件必须用 /select, 且路径要带引号。
        string argument;
        if (Directory.Exists(target))
        {
            argument = target;
        }
        else if (File.Exists(target))
        {
            argument = $"/select,\"{target}\"";
        }
        else
        {
            throw new FileNotFoundException($"路径不存在：{target}", target);
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开资源管理器失败 {Path}", target);
            throw;
        }
    }

    /// <inheritdoc />
    public void OpenUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new ArgumentException("地址不能为空。", nameof(uri));
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            _logger.LogInformation("已交给系统打开：{Uri}", uri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开地址失败 {Uri}", uri);
            throw;
        }
    }

    private static string RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。", nameof(path));
        }

        return path.Trim().Trim('"');
    }
}
