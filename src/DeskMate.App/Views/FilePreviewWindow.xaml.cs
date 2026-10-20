using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskMate.App.ViewModels;

namespace DeskMate.App.Views;

/// <summary>
/// 文件内容的放大窗口。单例：放大→关掉→再放大应该回到同一个窗口位置，
/// 而不是每次新开一个（关掉是 <c>Hide()</c>，因为 WPF 的单例窗口 Close 之后再 Show 会抛）。
/// </summary>
public partial class FilePreviewWindow : Window
{
    /// <summary>构造放大窗口。</summary>
    public FilePreviewWindow()
    {
        InitializeComponent();
    }

    /// <summary>这次呼出是不是「按住空格」带来的 —— 松手就该自动收。</summary>
    public bool IsHeld { get; private set; }

    /// <summary>「按住空格」模式下松手关掉时通知主窗清掉它那边的标记。</summary>
    public event Action? HoldReleased;

    /// <summary>
    /// 看某个文件的内容。传进来的视图模型与窄栏是同一个，两边翻页会互相同步。
    /// </summary>
    /// <param name="hold">
    /// <c>true</c> = 「按住空格」呼出：不抢焦点（抢了主窗就收不到 KeyUp），并且这一侧自己也要能收 KeyUp。
    /// 谁最终会拿到那一下抬起，取决于系统把激活给了谁，两边都备一条路才不会赖在屏幕上。
    /// </param>
    public void ShowFor(FilePreviewViewModel preview, Window? owner, bool hold = false)
    {
        IsHeld = hold;
        DataContext = preview;
        Owner = owner;
        ShowActivated = !hold;

        if (!IsVisible)
        {
            Show();
        }

        if (!hold)
        {
            // 已经开着但被人压住时，再点一次「放大」也要把它带到最前。
            Activate();
        }
    }

    /// <summary>收起。</summary>
    public void Dismiss()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        // 只有空白处能拖着走：点在按钮上的时候那一下是按钮的，抢过来按钮就点不着了。
        if (e.ButtonState == MouseButtonState.Pressed && e.Source is not Button)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        IsHeld = false;
        Dismiss();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsHeld = false;
            Dismiss();
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (e.Key == Key.Space && IsHeld)
        {
            ReleaseHold();
            e.Handled = true;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // 按住呼出之后焦点又被人抢走（Alt+Tab、弹窗跳出来），就再也等不到那一下抬起。
        if (IsHeld)
        {
            ReleaseHold();
        }
    }

    /// <summary>松手：收窗并告诉主窗「这条按住记录我结了」。</summary>
    private void ReleaseHold()
    {
        IsHeld = false;
        Dismiss();
        HoldReleased?.Invoke();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 单例窗口关成隐藏，和文件夹面板同一条规矩。PDF 不在这里释放：窄栏可能还在显示同一份内容。
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
