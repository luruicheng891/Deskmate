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
    /// <summary>等接手窗口出现的时长与次数：单实例应用把文件转交给老实例要一点时间。</summary>
    private const int HandoffAttempts = 5;

    private const int HandoffIntervalMs = 180;

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

        return ShellAssociation.ExecutableFor(Path.GetExtension(target)) is not null;
    }

    /// <inheritdoc />
    public void Launch(string path)
    {
        var target = RequirePath(path);
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            throw new FileNotFoundException($"找不到要启动的项目：{target}", target);
        }

        var handler = Directory.Exists(target)
            ? null
            : ShellAssociation.ExecutableFor(Path.GetExtension(target));

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            _logger.LogInformation("已启动 {Path}", target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动失败 {Path}", target);
            throw;
        }

        if (handler is not null)
        {
            RaiseHandlerWindowSoon(handler);
        }
    }

    /// <summary>
    /// 启动之后把接手的窗口抬到前台。
    /// <para>
    /// 为什么需要这一刀：单实例的应用（本机是 Typora）收到第二个实例转交的文件后，
    /// 只是更新自己那个已存在窗口的内容就完事，新进程立刻退出 —— 从我们这边看是「启动成功」，
    /// 用户那边双击 .md 却「什么都没发生」。老窗口可能最小化着、也可能被别的窗口压着。
    /// </para>
    /// <para>
    /// 只做「还原 + 抬起」，绝不做任务栏那套 <c>ToggleForeground</c>：那个语义是「点已经在前台的图标就收起」，
    /// 借过来会变成「双击打开文件，结果文件窗口被最小化」。
    /// </para>
    /// </summary>
    private void RaiseHandlerWindowSoon(string handler)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < HandoffAttempts; attempt++)
            {
                await Task.Delay(HandoffIntervalMs);

                var windows = await _windows.GetWindowsAsync(handler);
                if (windows.Count == 0)
                {
                    continue; // 窗口还没建出来
                }

                var foreground = WindowInterop.GetForeground();
                foreach (var window in windows)
                {
                    if (window.Handle == foreground)
                    {
                        return; // 已经在前台了，别乱动
                    }
                }

                foreach (var window in windows)
                {
                    if (WindowActivator.Activate(window.Handle))
                    {
                        _logger.LogDebug("已把接手 {Handler} 的窗口抬到前台", Path.GetFileName(handler));
                        return;
                    }
                }

                return;
            }

            _logger.LogDebug("启动 {Handler} 后没等到它的窗口（可能本来就没有窗口）", Path.GetFileName(handler));
        });
    }

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
