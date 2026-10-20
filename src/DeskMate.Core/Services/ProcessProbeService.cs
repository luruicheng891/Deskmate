using System.Diagnostics;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 进程探活实现。只读系统进程列表，不启动、不结束任何进程。
/// </summary>
public sealed class ProcessProbeService : IProcessProbeService
{
    private readonly StaScheduler _sta;
    private readonly ILogger<ProcessProbeService> _logger;

    /// <summary>构造探活服务。</summary>
    public ProcessProbeService(StaScheduler sta, ILogger<ProcessProbeService> logger)
    {
        _sta = sta;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> ResolveExecutablePathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var resolved = path;

        if (ShellLink.IsShortcut(path))
        {
            // 解析 .lnk 是 COM 调用，必须放到 STA 线程。
            var target = await _sta.RunAsync(() => ShellLink.ResolveTarget(path));
            if (string.IsNullOrWhiteSpace(target))
            {
                _logger.LogDebug("快捷方式 {Path} 解析不到目标，按自身路径判断", path);
            }
            else
            {
                resolved = target;
            }
        }

        // 环境变量要展开（目标常写成 %windir%\...），8.3 短路径要还原：
        // 快捷方式里存 QODERC~1.EXE 这种形式很常见，拿它去比进程名永远对不上，
        // 结果就是运行指示不亮、悬停没预览、点一下又开一个实例。
        return PathInterop.GetLongPath(Environment.ExpandEnvironmentVariables(resolved).Trim().Trim('"'));
    }

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> SnapshotRunningNamesAsync() => Task.Run<IReadOnlySet<string>>(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processes = Array.Empty<Process>();
        try
        {
            processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                // ProcessName 不需要提权就能拿到；ProcessPath 在很多系统进程上会抛 UnauthorizedAccess。
                names.Add(process.ProcessName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "进程快照只取到部分结果");
        }
        finally
        {
            // Process 是 IDisposable，每个实例都占着内核句柄，不释放会随轮询次数线性泄漏。
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return names;
    });

    /// <inheritdoc />
    public bool IsRunning(string executablePath, IReadOnlySet<string> runningNames)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath);
        return name.Length > 0 && runningNames.Contains(name);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunningApp>> SnapshotRunningAppsAsync() => Task.Run<IReadOnlyList<RunningApp>>(() =>
    {
        // 判据是「这个进程有没有一个算作任务的窗口」，不是 Process.MainWindowHandle。
        // 后者太松：PixPin 常驻后台时也会报一个 1x1 的窗口，TextInputHost 报的是输入法浮层，
        // 而任务栏溢出窗、IME 宿主这类也没有主窗口 —— 用窗口判定能一次性把这些都排除掉。
        var windowsByProcess = WindowDiscovery.GetWindowsGroupedByProcess();

        // 同一个程序常开多个进程（浏览器、Electron 全家桶），按 exe 路径归并，只留启动最早的那个。
        var byPath = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        var processes = Array.Empty<Process>();

        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "应用快照取不到进程列表");
            return [];
        }

        try
        {
            foreach (var process in processes)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                if (!windowsByProcess.TryGetValue((uint)process.Id, out var windows) || windows.Count == 0)
                {
                    continue;
                }

                var path = SafeMainModulePath(process);
                if (path is null)
                {
                    continue;
                }

                var started = SafeStartTime(process);
                if (byPath.TryGetValue(path, out var existing)
                    && (existing.StartedAt ?? DateTime.MaxValue) <= (started ?? DateTime.MaxValue))
                {
                    continue;
                }

                byPath[path] = new RunningApp(path, DisplayNameOf(path, process.ProcessName), started);
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return byPath.Values
            .OrderBy(app => app.StartedAt ?? DateTime.MaxValue)
            .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    });

    /// <summary>主模块路径。32/64 位不匹配或没权限的进程读不到，跳过而不是报错。</summary>
    private static string? SafeMainModulePath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return PathInterop.GetLongPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime? SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception)
        {
            // 受保护进程与已被系统回收的进程都会抛，排序时退到名字序。
            return null;
        }
    }

    // 显示名要的是「微信」「网易云音乐」这种产品名，进程名（Weixin）太硬。
    // 文件版本信息要读盘，所以按路径缓存一份。
    private static readonly ConcurrentDictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase);

    private static string DisplayNameOf(string path, string fallback)
        => DisplayNames.GetOrAdd(path, key =>
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(key).FileDescription;
                return string.IsNullOrWhiteSpace(description) ? fallback : description;
            }
            catch (Exception)
            {
                return fallback;
            }
        });
}
