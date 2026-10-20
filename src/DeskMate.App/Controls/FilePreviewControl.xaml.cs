using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMate.App.ViewModels;

namespace DeskMate.App.Controls;

/// <summary>
/// 文件内容预览的正文区。右侧预览栏与放大窗口共用这一份 ——
/// 同一份文件在两处看到的排版必须一致，抄两份模板迟早会走样。
/// </summary>
public partial class FilePreviewControl : UserControl
{
    /// <summary>构造控件；内容取自 DataContext 里的 <see cref="FilePreviewViewModel"/>。</summary>
    public FilePreviewControl()
    {
        InitializeComponent();

        // DataContextChanged 是事件而不是可重写的虚方法（FrameworkElement 上只有这个口子）。
        DataContextChanged += (_, _) => ScrollCurrentChipIntoViewLater();
    }

    /// <summary>
    /// 换文件之后把当前那枚胶囊滚进视野。不滚的话高亮的那枚可能停在横向另一端，
    /// 用户看到的是一排行到末尾的文件，以为「切过去没反应」。
    /// </summary>
    private void ScrollCurrentChipIntoViewLater()
    {
        if (ChipHost is null)
        {
            return;
        }

        // 容器要等布局生成完才拿得到，Loaded 优先级正好卡在「量完了但还没画下一帧」。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var current = ChipHost.Items.OfType<FileChipViewModel>().FirstOrDefault(chip => chip.IsCurrent);

            if (current is not null && ChipHost.ItemContainerGenerator.ContainerFromItem(current) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// PDF 的渲染尺寸只能由界面报上来：视图模型不知道自己会被摆多宽，
    /// 而「渲多少像素」必须按显示宽度 × 系统缩放算，否则就是拿一张大图让 WPF 缩小，字会糊。
    /// </summary>
    private void OnPdfViewportChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewport || DataContext is not FilePreviewViewModel preview)
        {
            return;
        }

        // 减掉内边距：ScrollViewer 的 ActualWidth 含 Padding，按它渲会宽出一条横向滚动条。
        var usable = e.NewSize.Width - viewport.Padding.Left - viewport.Padding.Right;

        preview.SetPdfViewport(usable, VisualTreeHelper.GetDpi(viewport).DpiScaleX);
    }
}
