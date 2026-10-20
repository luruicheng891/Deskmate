using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DeskMate.App.Views;

/// <summary>
/// 非模态提示浮层。取代原先各处操作结果都弹一个模态对话框的写法：
/// 浮层不抢焦点、不等用户点确定，多条提示叠在一起，到期自动收掉。
/// </summary>
public partial class ToastWindow : Window
{
    /// <summary>普通提示停留时间。</summary>
    private const int VisibleMs = 2600;

    /// <summary>错误停留稍长，用户可能正在处理别的事。</summary>
    private const int ErrorVisibleMs = 3600;

    /// <summary>同时最多显示几条，超出的先把最旧的一条挤掉。</summary>
    private const int MaxStack = 3;

    // 同一句话短时间内重复出现（连点、批量循环）只保留一条并续期，不然浮层会刷屏。
    private const int DuplicateWindowMs = 1200;

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(160);

    private readonly DispatcherTimer _pruneTimer;
    private readonly ObservableCollection<ToastItem> _items = new();
    private bool _hiding;

    /// <summary>构造提示浮层。由 <c>ToastService</c> 懒创建并常驻复用，永远不 Close。</summary>
    public ToastWindow()
    {
        InitializeComponent();

        Host.ItemsSource = _items;

        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _pruneTimer.Tick += (_, _) => PruneExpired();
    }

    /// <summary>当前有没有正在显示的提示（自检用）。</summary>
    public bool IsShowing => IsVisible && _items.Count > 0;

    /// <summary>推入一条提示。</summary>
    /// <param name="message">文案。</param>
    /// <param name="accent">左侧色条，由主题色板取。</param>
    /// <param name="isError">是不是错误（错误停留更久）。</param>
    /// <param name="actionLabel">可选动作按钮文字，比如「撤销」。</param>
    /// <param name="action">可选动作本体，按下后这条提示收起。</param>
    public void Push(string message, Brush accent, bool isError, string? actionLabel = null, Action? action = null)
    {
        var now = DateTime.UtcNow;
        var ttl = TimeSpan.FromMilliseconds(isError ? ErrorVisibleMs : VisibleMs);

        var duplicate = _items.FirstOrDefault(item => item.Message == message);
        if (duplicate is not null && now - duplicate.PushedAt < TimeSpan.FromMilliseconds(DuplicateWindowMs))
        {
            // 重复的那条只续期，不再叠一份，否则批量操作会把屏幕刷成一列同样的字。
            duplicate.PushedAt = now;
            duplicate.ExpiresAt = now + ttl;
            return;
        }

        _items.Add(new ToastItem(message, accent, now, now + ttl, actionLabel, action));

        while (_items.Count > MaxStack)
        {
            _items.RemoveAt(0);
        }

        // 上一条正在淡出时又来了新的：直接把它拉回不透明，别让收尾动画连新的一条一起抹掉。
        if (_hiding || Opacity < 0.99)
        {
            _hiding = false;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration));
        }

        _pruneTimer.Start();
    }

    /// <summary>立刻收掉全部提示（自检收尾用）。</summary>
    public void DismissAll()
    {
        _pruneTimer.Stop();
        _items.Clear();
        FadeOutAndHide();
    }

    /// <summary>淡入。调用方负责在这之前把 Opacity 压到 0 再 Show，否则第一帧是不透明的，读起来像闪一下。</summary>
    public void FadeIn()
    {
        _hiding = false;
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration));
    }

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        var taken = false;

        for (var index = _items.Count - 1; index >= 0; index--)
        {
            if (_items[index].ExpiresAt > now)
            {
                continue;
            }

            _items.RemoveAt(index);
            taken = true;
        }

        if (!taken)
        {
            return;
        }

        if (_items.Count == 0)
        {
            _pruneTimer.Stop();
            FadeOutAndHide();
        }
    }

    private void FadeOutAndHide()
    {
        if (_hiding)
        {
            return;
        }

        _hiding = true;

        // 只等动画的 Completed 不可靠（这台机器上真遇过动画没走表、Completed 永远不来的情况，
        // 结果是窗口关不掉），所以并一个一次性定时器兜底，两条路径谁先到算谁。
        var fallback = new DispatcherTimer { Interval = FadeDuration + TimeSpan.FromMilliseconds(300) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            HideNow();
        };
        fallback.Start();

        var fade = new DoubleAnimation(0, FadeDuration);
        fade.Completed += (_, _) =>
        {
            fallback.Stop();
            HideNow();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void HideNow()
    {
        if (!_hiding)
        {
            return;
        }

        _hiding = false;

        if (_items.Count == 0)
        {
            Hide();
        }
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ToastItem item })
        {
            return;
        }

        _items.Remove(item);

        if (_items.Count == 0)
        {
            _pruneTimer.Stop();
            FadeOutAndHide();
        }
    }
    /// <summary>提示上的动作按钮（「撤销」这类）。按钮自己吃掉鼠标事件，不会顺带把整条提示点掉两次。</summary>
    private void OnActionClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: ToastItem item })
        {
            return;
        }

        _items.Remove(item);
        item.Action?.Invoke();

        if (_items.Count == 0)
        {
            _pruneTimer.Stop();
            FadeOutAndHide();
        }
    }
}

/// <summary>浮层里的一条提示。</summary>
/// <param name="Message">文案。</param>
/// <param name="Accent">左色条画刷。</param>
/// <param name="PushedAt">推入时刻，用于判重。</param>
/// <param name="ExpiresAt">自动消失时刻。</param>
/// <param name="ActionLabel">动作按钮文字，没有动作时为 null。</param>
/// <param name="Action">动作本体。</param>
public sealed class ToastItem(
    string message,
    Brush accent,
    DateTime pushedAt,
    DateTime expiresAt,
    string? actionLabel = null,
    Action? action = null)
{
    /// <summary>文案。</summary>
    public string Message { get; } = message;

    /// <summary>左色条画刷。</summary>
    public Brush Accent { get; } = accent;

    /// <summary>推入时刻。</summary>
    public DateTime PushedAt { get; set; } = pushedAt;

    /// <summary>到期时刻，可被重复提示续期。</summary>
    public DateTime ExpiresAt { get; set; } = expiresAt;

    /// <summary>动作按钮文字。</summary>
    public string? ActionLabel { get; } = actionLabel;

    /// <summary>动作本体。</summary>
    public Action? Action { get; } = action;

    /// <summary>没动作时整个按钮不占位。</summary>
    public Visibility ActionVisibility => Action is null || ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
}
