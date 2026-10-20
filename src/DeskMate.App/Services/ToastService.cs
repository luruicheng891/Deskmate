using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMate.App.Views;
using DeskMate.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DeskMate.App.Services;

/// <summary>提示的分量，决定左色条与停留时长。</summary>
public enum ToastKind
{
    /// <summary>普通回执。</summary>
    Info,

    /// <summary>成功。</summary>
    Success,

    /// <summary>部分成功、被忽略、需要留意。</summary>
    Warning,

    /// <summary>失败。</summary>
    Error,
}

/// <summary>
/// 视图模型抛给用户的一条消息。VM 只负责说话，用什么界面说由 <see cref="ToastService"/> 决定，
/// 所以这里默认是失败 —— 现有调用点几乎全是失败回执。
/// </summary>
/// <param name="Message">文案。</param>
/// <param name="Kind">分量，决定色条与停留时长。</param>
/// <param name="ActionLabel">可选：附带一个动作按钮的文字，比如「撤销」。</param>
/// <param name="Action">可选：按钮按下要执行的动作。</param>
public sealed class NoticeEventArgs(
    string message,
    ToastKind kind = ToastKind.Error,
    string? actionLabel = null,
    Action? action = null) : EventArgs
{
    /// <summary>文案。</summary>
    public string Message { get; } = message;

    /// <summary>分量。</summary>
    public ToastKind Kind { get; } = kind;

    /// <summary>动作按钮文字，没动作时为 null。</summary>
    public string? ActionLabel { get; } = actionLabel;

    /// <summary>动作本体。</summary>
    public Action? Action { get; } = action;
}

/// <summary>
/// 非模态提示的统一出口。
/// <para>
/// 原先每个窗口都把 <c>UserNotice</c> 接到 <c>MessageBox.Show</c>：一个常驻桌面工具每做一步
/// 都要用户点一次确定，而且 Dock 是无焦点置顶窗口，模态框还会抢走别的程序的焦点，
/// 用户的观感就是「点了一下没反应」。这里统一改成不抢焦点、自动消失的浮层。
/// </para>
/// </summary>
public sealed class ToastService
{
    private const double EdgeGap = 12;

    private readonly IServiceProvider _services;
    private readonly IConfigService _config;
    private readonly Dispatcher _dispatcher;
    private ToastWindow? _window;

    /// <summary>构造提示服务。必须在 UI 线程上由容器创建。</summary>
    public ToastService(IServiceProvider services, IConfigService config)
    {
        _services = services;
        _config = config;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>当前是否有提示正在显示（自检用）。</summary>
    public bool IsShowing => _window?.IsShowing == true;

    /// <summary>普通回执。</summary>
    public void Info(string message) => Show(message, ToastKind.Info);

    /// <summary>成功回执。</summary>
    public void Success(string message) => Show(message, ToastKind.Success);

    /// <summary>需要留意（部分成功、被忽略）。</summary>
    public void Warning(string message) => Show(message, ToastKind.Warning);

    /// <summary>失败。</summary>
    public void Error(string message) => Show(message, ToastKind.Error);

    /// <summary>推入一条提示。从任何线程调用都可以，内部转回 UI 线程。</summary>
    /// <param name="message">文案。</param>
    /// <param name="kind">分量。</param>
    /// <param name="actionLabel">可选动作按钮文字（「撤销」这类）。</param>
    /// <param name="action">可选动作本体。</param>
    public void Show(string message, ToastKind kind = ToastKind.Info, string? actionLabel = null, Action? action = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            ShowCore(message, kind, actionLabel, action);
            return;
        }

        _dispatcher.BeginInvoke(() => ShowCore(message, kind, actionLabel, action));
    }

    /// <summary>收掉全部提示（自检收尾用）。</summary>
    public void DismissAll()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(DismissAll);
            return;
        }

        _window?.DismissAll();
    }

    /// <summary>
    /// 取当前浮层的抓屏矩形（物理像素，四周各留 24px 便于肉眼判断它压在哪里）。
    /// <para>自检要靠这个截一帧存盘：浮层「没抛异常」和「真的画出来了、位置对」是两回事。</para>
    /// </summary>
    public bool TryGetCaptureFrame(out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;

        if (_window is not { IsVisible: true } window || window.ActualWidth < 1 || window.ActualHeight < 1)
        {
            return false;
        }

        var scale = VisualTreeHelper.GetDpi(window).PixelsPerDip;
        const int margin = 24;

        x = (int)Math.Floor((window.Left - margin) * scale);
        y = (int)Math.Floor((window.Top - margin) * scale);
        width = (int)Math.Ceiling((window.ActualWidth + margin * 2) * scale);
        height = (int)Math.Ceiling((window.ActualHeight + margin * 2) * scale);
        return width > 0 && height > 0;
    }

    private void ShowCore(string message, ToastKind kind, string? actionLabel, Action? action)
    {
        var window = EnsureWindow();

        window.Push(message, AccentFor(kind), kind == ToastKind.Error, actionLabel, action);

        if (!window.IsVisible)
        {
            // 先把透明度压到 0 再 Show：Show 的第一帧按当前不透明度画，之后才从 0 淡入，
            // 顺序反了就被用户看成「闪一下」。
            window.Opacity = 0;
            window.Show();
            window.FadeIn();
        }

        // 尺寸由内容决定，必须在 Push 之后重新量一次才拿得到真实的 ActualHeight。
        window.UpdateLayout();
        Position(window);
    }

    private ToastWindow EnsureWindow() => _window ??= new ToastWindow();

    private Brush AccentFor(ToastKind kind)
    {
        var key = kind switch
        {
            ToastKind.Success => "SuccessBrush",
            ToastKind.Warning => "WarningBrush",
            ToastKind.Error => "InvalidBrush",
            _ => "AccentBrush",
        };

        return Application.Current.TryFindResource(key) as SolidColorBrush ?? Brushes.Gray;
    }

    /// <summary>
    /// 贴在 Dock 上方居中：动作是从 Dock 上做的，提示就该出现在手边。
    /// Dock 不可见（被隐藏或全屏让路）时退回工作区底部居中。
    /// </summary>
    private void Position(ToastWindow window)
    {
        var work = SystemParameters.WorkArea;
        var anchorBottom = work.Bottom;
        var centerX = work.Left + work.Width / 2;

        if (_services.GetService<DockWindow>() is { IsVisible: true } dock && _config.Current.DockPosition == "Bottom")
        {
            anchorBottom = dock.Top;
            centerX = dock.Left + dock.ActualWidth / 2;
        }

        var left = centerX - window.ActualWidth / 2;
        var top = anchorBottom - window.ActualHeight - EdgeGap;

        window.Left = Math.Round(Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - window.ActualWidth)));
        window.Top = Math.Round(Math.Clamp(top, work.Top, work.Bottom - window.ActualHeight));
    }
}
