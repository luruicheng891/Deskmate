using System.Windows;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// Dock 运行区的数据源：只收「真的有任务窗口」的进程，并且永远不含 DeskMate 自己。
/// </summary>
public class ProcessProbeRunningAppsTests : IDisposable
{
    private readonly StaScheduler _sta = new();
    private Window? _window;

    [Fact]
    public async Task Snapshot_ExcludesThisProcess_EvenWhenItHasARealWindow()
    {
        // 不先开一个窗口，这个断言就是白给的：没窗口的进程本来也不会进快照。
        _window = await _sta.RunAsync(() =>
        {
            var window = new Window { Title = "DeskMate 探针窗口", Width = 400, Height = 300 };
            window.Show();
            return window;
        });

        var apps = await Create().SnapshotRunningAppsAsync();

        Assert.DoesNotContain(apps, app => string.Equals(
            app.ExecutablePath, PathInterop.GetLongPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Snapshot_OnlyContainsAppsWithSwitchableWindows()
    {
        var apps = await Create().SnapshotRunningAppsAsync();
        var withWindows = WindowDiscovery.GetWindowsGroupedByProcess()
            .Select(pair => PathInterop.GetLongPath(ModuleOf(pair.Key) ?? string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            Assert.True(File.Exists(app.ExecutablePath), $"快照里有不存在的路径：{app.ExecutablePath}");
            Assert.False(string.IsNullOrWhiteSpace(app.DisplayName));
            Assert.Contains(app.ExecutablePath, withWindows);
        }
    }

    public void Dispose()
    {
        if (_window is not null)
        {
            _ = _sta.RunAsync(() =>
            {
                _window.Close();
                return true;
            }).GetAwaiter().GetResult();
        }

        _sta.Dispose();
    }

    private ProcessProbeService Create() => new(_sta, NullLogger<ProcessProbeService>.Instance);

    private static string? ModuleOf(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
