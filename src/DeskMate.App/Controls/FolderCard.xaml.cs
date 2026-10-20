namespace DeskMate.App.Controls;

/// <summary>
/// 文件夹卡片：路径引用的可视化单元。
/// 点击与拖拽由 <c>FolderManagerWindow</c> 统一处理，本控件只负责呈现与右键菜单。
/// </summary>
public partial class FolderCard : System.Windows.Controls.UserControl
{
    /// <summary>构造文件夹卡片。</summary>
    public FolderCard() => InitializeComponent();
}
