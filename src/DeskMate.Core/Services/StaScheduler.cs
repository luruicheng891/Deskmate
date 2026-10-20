using System.Threading;
using System.Windows.Threading;

namespace DeskMate.Core.Services;

/// <summary>
/// 一条常驻 STA 线程 + Dispatcher。图标提取与 .lnk 解析必须在上面执行，原因有两个：
/// ShellLink 是 COM 对象，且 HICON 转 BitmapSource 会创建 DispatcherObject，
/// 二者在纯线程池线程上行为不稳定。放进去、Freeze 后再交给 UI 线程使用。
/// </summary>
public sealed class StaScheduler : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Dispatcher _dispatcher = null!;

    /// <summary>启动 STA 线程并等待其 Dispatcher 就绪。</summary>
    public StaScheduler()
    {
        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        })
        {
            Name = "DeskMate.STA",
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>在 STA 线程上执行并返回结果，异常会原样传播给调用方。</summary>
    public Task<T> RunAsync<T>(Func<T> work) => _dispatcher.InvokeAsync(work).Task;

    /// <summary>在 STA 线程上执行无返回值的操作。</summary>
    public Task RunAsync(Action work) => _dispatcher.InvokeAsync(work).Task;

    /// <summary>关闭 Dispatcher，线程随进程退出。</summary>
    public void Dispose()
    {
        if (_dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.InvokeShutdown();
        _ready.Dispose();
    }
}
