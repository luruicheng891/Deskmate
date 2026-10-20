using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeskMate.App.ViewModels;

namespace DeskMate.App.Views;

/// <summary>
/// 全局搜索窗口：Alt+Space 呼出，居中偏上，失焦自动关闭。
/// </summary>
public partial class SearchWindow : Window
{
    private const double PanelWidth = 600;
    private const double OpenScaleFrom = 0.95;

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(140);

    private readonly SearchViewModel _viewModel;
    private bool _contextMenuOpen;

    /// <summary>构造搜索窗口。</summary>
    public SearchWindow(SearchViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) => Dismiss();
        ResultsHost.PreviewMouseMove += OnResultsMouseMove;
    }

    /// <summary>快捷键切换：已在前台则关闭，否则呼出。</summary>
    public void Toggle()
    {
        if (IsVisible && IsActive)
        {
            Dismiss();
            return;
        }

        Present();
    }

    /// <summary>呼出并聚焦输入框。</summary>
    public void Present()
    {
        // 横向按固定宽度居中，纵向落在屏幕上方约 1/5 处，接近系统聚焦搜索的视觉位置。
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - PanelWidth) / 2);
        Top = SystemParameters.PrimaryScreenHeight * 0.18;

        Show();
        Activate();

        _viewModel.PrepareForInput();
        InputBox.Focus();
        Keyboard.Focus(InputBox);
        AnimateOpen();
    }

    /// <summary>收起（保留实例，下次呼出无需重建）。</summary>
    public void Dismiss()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // 右键菜单会短暂抢走焦点，这时候不能把自己关掉，否则菜单一点就整个消失。
        if (_contextMenuOpen)
        {
            return;
        }

        // 需求 M4：失焦自动关闭。
        Dismiss();
    }

    private void OnResultMenuOpened(object sender, RoutedEventArgs e) => _contextMenuOpen = true;

    private void OnResultMenuClosed(object sender, RoutedEventArgs e) => _contextMenuOpen = false;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+Enter 要先判：它和回车是同一个键，放到 switch 里就分不开了。
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _viewModel.RevealSelected();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                _viewModel.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.ChooseSelected();
                e.Handled = true;
                break;
            default:
                base.OnPreviewKeyDown(e);
                break;
        }
    }

    private void AnimateOpen()
    {
        var ease = new CircleEase { EasingMode = EasingMode.EaseOut };

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, OpenDuration) { EasingFunction = ease });

        PanelScale.ScaleX = OpenScaleFrom;
        PanelScale.ScaleY = OpenScaleFrom;
        PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, OpenDuration) { EasingFunction = ease });
        PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, OpenDuration) { EasingFunction = ease });
    }

    private void OnResultsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ResolveHit(source) is { } hit)
        {
            _viewModel.SelectHit(hit);
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchHitViewModel hit })
        {
            _viewModel.Choose(hit);
            e.Handled = true;
        }
    }

    private static SearchHitViewModel? ResolveHit(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: SearchHitViewModel hit })
            {
                return hit;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

}
