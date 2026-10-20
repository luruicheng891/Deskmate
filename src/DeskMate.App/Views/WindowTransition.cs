using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DeskMate.App.Views;

/// <summary>
/// 窗口开合的过渡动画。
/// <para>
/// 淡入淡出有两种落点，必须分清：<see cref="PlayLauncher"/> 动整窗透明度，只有
/// <c>AllowsTransparency=True</c> 的分层窗口能用；普通窗口一动就翻车 —— WPF 会把窗口临时
/// 转成分层窗口来合成，中间那几帧客户区没有内容可合成，屏幕上就是一个黑框（实测踩过）。
/// 普通窗口走 <see cref="PlayContentLevel"/>：窗口本身保持实心，只淡内容层。
/// </para>
/// </summary>
internal static class WindowTransition
{
    // 淡入比位移/缩放先结束：内容早点看清，回弹只是收尾的余味。
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(340);

    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(300);

    private const double LauncherOpenScale = 0.86;

    private const double LauncherCloseScale = 0.9;

    // 面板内容全是要点的东西，缩放幅度必须小：开合期间元素位置一直在动，凭位置去点会点偏。
    private const double PanelOpenScale = 0.96;

    private const double LauncherOpenDip = 14;

    private const double LauncherCloseDip = 20;

    /// <summary>回弹超过 1 的那一小截，换算成缩放；再大就会把面板顶出窗口边界被裁掉。</summary>
    private const double SpringOvershoot = 0.022;

    /// <summary>
    /// 启动台。开：以「点下来的那个位置」为锚点缩放长出来 + 轻微上浮 + 淡入，收尾带回弹；
    /// 关：收回面板中心 + 下沉 + 淡出，模拟 macOS 把窗口收进 Dock。
    /// <paramref name="origin"/> 是相对面板的归一化原点，允许超出 [0,1] —— Dock 在面板下方时 Y 就是 1 点几，
    /// 这正是「从图标那里长出来」的方向。
    /// </summary>
    public static void PlayLauncher(Window window, FrameworkElement content, Point origin, bool showing, Action? onHidden = null)
    {
        var (scale, slide) = EnsureTransform(content);

        if (showing)
        {
            // 先把不透明度压到 0 再 Show：否则第一帧按 1 画出来，接着才从 0 开始淡入，看起来就是「闪一下」。
            window.Opacity = 0;
            Fade(window, Window.OpacityProperty, 0, 1, FadeDuration, EasingMode.EaseOut);
            content.RenderTransformOrigin = origin;
            Spring(scale, ScaleTransform.ScaleXProperty, LauncherOpenScale, 1, SpringOvershoot);
            Spring(scale, ScaleTransform.ScaleYProperty, LauncherOpenScale, 1, SpringOvershoot);
            Spring(slide, TranslateTransform.YProperty, LauncherOpenDip, 0, LauncherOpenDip * 0.12);
            return;
        }

        // 淡出与收缩同时结束（都是 CloseDuration），只淡一半就停住会读成「卡了一下」。
        var fade = Fade(window, Window.OpacityProperty, window.Opacity, 0, CloseDuration, EasingMode.EaseIn);
        content.RenderTransformOrigin = new Point(0.5, 0.5);
        Sink(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, LauncherCloseScale);
        Sink(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, LauncherCloseScale);
        Sink(slide, TranslateTransform.YProperty, slide.Y, LauncherCloseDip);

        if (onHidden is not null)
        {
            WhenSettled(fade, CloseDuration, onHidden);
        }
    }

    /// <summary>
    /// 内容层淡入 + 轻微回弹：普通窗口（文件夹管理）打开时用。窗口底色始终实心，所以不会出现分层窗口那种黑框。
    /// <para>
    /// 这里刻意<strong>没有</strong>关闭方向：淡出淡的是内容层，露出来的是窗口自己那块实心底色，
    /// 屏幕上会先出现一个「同样大小、里面空空的框」再消失（用户报的「后面还有一个同等大小的框」）。
    /// 关闭直接 <c>Hide()</c> —— 那一刻用户要的是它马上不见，不是它优雅地淡走。
    /// </para>
    /// </summary>
    public static void PlayContentIn(FrameworkElement content)
    {
        var (scale, _) = EnsureTransform(content);
        content.RenderTransformOrigin = new Point(0.5, 0.5);

        content.Opacity = 0;
        Fade(content, UIElement.OpacityProperty, 0, 1, FadeDuration, EasingMode.EaseOut);
        Spring(scale, ScaleTransform.ScaleXProperty, PanelOpenScale, 1, SpringOvershoot);
        Spring(scale, ScaleTransform.ScaleYProperty, PanelOpenScale, 1, SpringOvershoot);
    }


    /// <summary>
    /// 淡出结束后要做的事，走「动画 Completed + 一个兜底定时器」两条路，谁先到算谁，且只执行一次。
    /// <para>
    /// 只等 <c>Completed</c> 是实测踩过的失败模式：淡出还没结束就重新呼出窗口时，新挂上的动画会把旧时钟换掉，
    /// 被换掉的时钟不会再触发 Completed —— 于是 <c>Hide()</c> 永远不执行，屏幕上留下一个内容层已经全透明、
    /// 但还挂在桌面上的空壳窗口，看着就像软件坏了。
    /// </para>
    /// </summary>
    private static void WhenSettled(AnimationTimeline clock, TimeSpan budget, Action onDone)
    {
        var done = false;

        void Finish()
        {
            if (done)
            {
                return;
            }

            done = true;
            onDone();
        }

        clock.Completed += (_, _) => Finish();

        var timer = new DispatcherTimer { Interval = budget + TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Finish();
        };
        timer.Start();
    }

    // 一个元素同时要吃缩放和位移，只能挂在 TransformGroup 上；只建一次，之后复用同一个实例，
    // 否则每次开合都会把上一层动画的时钟丢掉。
    private static (ScaleTransform Scale, TranslateTransform Slide) EnsureTransform(FrameworkElement content)
    {
        if (content.RenderTransform is TransformGroup existing
            && existing.Children.Count == 2
            && existing.Children[0] is ScaleTransform heldScale
            && existing.Children[1] is TranslateTransform heldSlide)
        {
            return (heldScale, heldSlide);
        }

        var group = new TransformGroup();
        var scale = new ScaleTransform(1, 1);
        var slide = new TranslateTransform();
        group.Children.Add(scale);
        group.Children.Add(slide);
        content.RenderTransform = group;
        return (scale, slide);
    }

    private static DoubleAnimation Fade(
        IAnimatable target, DependencyProperty property, double from, double to, TimeSpan duration, EasingMode mode)
        => Run(
            target,
            property,
            new DoubleAnimation(from, to, duration)
            {
                EasingFunction = Ease(mode),
            });

    /// <summary>开场：三段回弹（冲过头一点 → 收回来一点 → 落定），眼睛最容易读成「弹性」的就是这个收尾。</summary>
    private static void Spring(IAnimatable target, DependencyProperty property, double from, double to, double overshoot)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = OpenDuration };
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromPercent(0)) { EasingFunction = Ease(EasingMode.EaseOut) });
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to + overshoot, KeyTime.FromPercent(0.62)) { EasingFunction = Ease(EasingMode.EaseInOut) });
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to - overshoot * 0.35, KeyTime.FromPercent(0.82)) { EasingFunction = Ease(EasingMode.EaseInOut) });
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromPercent(1)));
        Run(target, property, frames);
    }

    /// <summary>收场：加速沉下去，越接近结束越快，读起来才像「被吸走」而不是「匀速飘走」。</summary>
    private static void Sink(IAnimatable target, DependencyProperty property, double from, double to)
    {
        Run(
            target,
            property,
            new DoubleAnimation(from, to, CloseDuration)
            {
                EasingFunction = Ease(EasingMode.EaseIn),
            });
    }

    private static CircleEase Ease(EasingMode mode) => new() { EasingMode = mode };

    // 走 IAnimatable 而不是具体类型：窗口、内容元素、ScaleTransform、TranslateTransform 都能挂动画，
    // 而它们各自的基类并不相同。泛型是为了把时钟原样交回去，淡出结束的回调用得到。
    //
    // 这里不要「先 BeginAnimation(property, null) 再起新时钟」：那条移除操作要等下一次渲染才生效，
    // 会把同一调用里刚挂上的时钟一起摘掉，属性就停在本地值上不动了 —— 淡入不跑（窗口一片空白）、
    // 淡出也不跑（Completed 不来，Hide 永远不执行，窗口关不掉）。实测踩过，别再加回去。
    private static T Run<T>(IAnimatable target, DependencyProperty property, T animation)
        where T : AnimationTimeline
    {
        target.BeginAnimation(property, animation);
        return animation;
    }
}
