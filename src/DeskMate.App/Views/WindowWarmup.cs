using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskMate.App.Views;

/// <summary>
/// 窗口开起来顺不顺，靠这两件事：把「第一次才要做的贵活」挪到启动后的空闲里先做掉，
/// 以及把开场动画挂在真的出帧之后。
/// <para>
/// 第一次 <c>Show()</c> 里含着整棵内容树的首次测量排列、窗口句柄创建与首次合成：实测文件夹面板 300ms、
/// 启动台 150~165ms。这段时间正好压在 340ms 的开场动画中间，而动画走的是真实时间 —— 于是人看到的是
/// 「屏幕先定住一下，然后面板已经涨满了啪地出现」。用户报的「打开很僵硬、卡卡的」主要是它。
/// </para>
/// </summary>
internal static class WindowWarmup
{
    /// <summary>按给定的内容尺寸先量一遍、排一遍。窗口已经显示或尺寸没准备好时什么都不做。</summary>
    public static void PreWarmLayout(Window window, Size size)
    {
        if (window.IsVisible)
        {
            return;
        }

        // 先把 HWND 建出来：Show() 里除了布局还有窗口创建，那部分只能真做过才算预热到位。
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();

        if (double.IsNaN(size.Width) || double.IsNaN(size.Height) || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (window.Content is not FrameworkElement content)
        {
            return;
        }

        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
    }

    /// <summary>
    /// 下一帧真的开始合成时做一次（「出帧」与一个兜底定时器两条路，谁先到算谁，且只执行一次）。
    /// <para>
    /// 开场动画不能挂在 <c>Show()</c> 上：首帧可能被「窗口刚从隐藏里回来」拖住一百多毫秒（渲染资源要重建、
    /// 阴影那层着色器要现编译），动画按真实时间走，那 150ms 照跑不误 —— 挂上去就是让人看半截开场。
    /// </para>
    /// <para>
    /// 为什么要兜底：<c>CompositionTarget.Rendering</c> 只在合成循环转起来的时候才响，屏幕睡着或被别的
    /// 程序独占时它可能一直不来；没有兜底就是按了快捷键界面永远不出现。
    /// </para>
    /// </summary>
    public static void WhenNextFrame(Action onRendered, int fallbackMilliseconds = 120)
    {
        var begun = false;

        void Begin()
        {
            if (begun)
            {
                return;
            }

            begun = true;
            onRendered();
        }

        void Handler(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= Handler;
            Begin();
        }

        CompositionTarget.Rendering += Handler;

        var failsafe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(fallbackMilliseconds) };
        failsafe.Tick += (_, _) =>
        {
            failsafe.Stop();
            CompositionTarget.Rendering -= Handler;
            Begin();
        };
        failsafe.Start();
    }
}
