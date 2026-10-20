using System.Windows;
using System.Windows.Interop;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 端到端验证「点已运行的应用 = 切回它的窗口」，以及刻意不去捞被应用自己藏起来的窗口。
/// <para>
/// 窗口是本测试在自己进程里真开出来的 WPF 窗口，按当前进程取窗口，不依赖任何外部程序。
/// 之前用记事本，但 22631 的 <c>System32\notepad.exe</c> 只是个转发壳：真正的窗口属于另一个进程，
/// 于是测试其实一直在断言桌面上碰巧开着的记事本，看着能过、什么都没测。
/// </para>
/// </summary>
public class WindowSwitchIntegrationTests : IDisposable
{
    private const string PanelTitle = "DeskMate 测试窗口";

    private readonly StaScheduler _sta = new();
    private readonly WindowService _service = new(NullLogger<WindowService>.Instance);
    private readonly List<Window> _windows = new();

    [Fact]
    public async Task GetWindowsAsync_FindsTheVisibleWindowOfThisProcess()
    {
        var handle = await CreateWindowAsync();

        var windows = await _service.GetWindowsAsync(Environment.ProcessPath!);

        Assert.Contains(windows, window => window.Handle == handle);
    }

    /// <summary>
    /// 应用把自己 hide() 掉之后就不再是候选，这是刻意的：从外面 ShowWindow 只会得到一个没有内容的白框
    /// —— 微信 4 的主面板就是这样（Qt 层的 widget 已经隐藏，不会再重绘），比再开一个实例更糟。
    /// </summary>
    [Fact]
    public async Task GetWindowsAsync_IgnoresWindowsTheApplicationHidItself()
    {
        var handle = await CreateWindowAsync();
        await HideAsync(handle);
        Assert.False(WindowInterop.IsVisible(handle));

        var windows = await _service.GetWindowsAsync(Environment.ProcessPath!);

        Assert.DoesNotContain(windows, window => window.Handle == handle);
    }

    public void Dispose()
    {
        foreach (var window in _windows)
        {
            try
            {
                // 窗口只能在创建它的 STA 线程上关；收尾失败也不影响结论，窗口随进程一起消失。
                _ = _sta.RunAsync(() =>
                {
                    window.Close();
                    return true;
                }).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
        }

        _sta.Dispose();
    }

    private Task<nint> CreateWindowAsync()
        => _sta.RunAsync(() =>
        {
            var window = new Window
            {
                Title = PanelTitle,
                Width = 420,
                Height = 320,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 60,
            };

            // Show 之后才有真实 HWND。
            window.Show();
            _windows.Add(window);

            return new WindowInteropHelper(window).Handle;
        });

    private Task<bool> HideAsync(nint handle)
        => _sta.RunAsync(() =>
        {
            _windows.FirstOrDefault(item => new WindowInteropHelper(item).Handle == handle)?.Hide();
            return true;
        });
}
