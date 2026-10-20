using System.Windows;
using System.Windows.Input;

namespace DeskMate.App.Controls;

/// <summary>
/// 悬停事件参数。<b>必须自带来源控件</b>：路由过程中 WPF 会把 <see cref="RoutedEventArgs.Source"/>
/// 改写成外层容器（实测变成 ItemsControl），而挂在本控件父级上的处理器拿到的 sender 又是父级本身，
/// 于是靠 sender / Source 找图标永远找不到，悬停预览会静默失效。
/// </summary>
public sealed class DockItemHoverEventArgs : RoutedEventArgs
{
    /// <summary>构造悬停事件参数。</summary>
    public DockItemHoverEventArgs(DockItemControl source)
        : base(DockItemControl.HoverChangedEvent, source) => Control = source;

    /// <summary>状态发生变化的图标控件。</summary>
    public DockItemControl Control { get; }
}

/// <summary>
/// Dock 上的一个图标单元：负责自身悬停缩放，并把悬停状态冒泡给
/// <see cref="DockWindow"/>，由窗口统一计算相邻图标的让位位移。
/// </summary>
public partial class DockItemControl : System.Windows.Controls.UserControl
{
    /// <summary>悬停状态变化（冒泡），携带本控件作为 Source。</summary>
    public static readonly RoutedEvent HoverChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(HoverChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DockItemControl));

    /// <summary>当前鼠标是否在本图标范围内。</summary>
    public bool IsPointerOver { get; private set; }

    /// <summary>构造 Dock 图标控件。</summary>
    public DockItemControl() => InitializeComponent();

    /// <summary>悬停状态变化事件。</summary>
    public event RoutedEventHandler HoverChanged
    {
        add => AddHandler(HoverChangedEvent, value);
        remove => RemoveHandler(HoverChangedEvent, value);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        IsPointerOver = true;
        RaiseEvent(new DockItemHoverEventArgs(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        IsPointerOver = false;
        RaiseEvent(new DockItemHoverEventArgs(this));
    }
}
