using System.Windows;

namespace DeskMate.App.Views;

/// <summary>
/// 一行文本的小输入框。
/// <para>
/// Dock 图标只有几十像素宽，放不下文件夹面板那种浮层，所以改名这类输入用一个居中的小模态框。
/// 回车等于「确定」（IsDefault），Esc 等于「取消」（IsCancel），不用再自己处理按键。
/// </para>
/// </summary>
public partial class TextInputWindow : Window
{
    private Func<string, string?> _validate = _ => null;

    private TextInputWindow()
    {
        InitializeComponent();
    }

    /// <summary>弹一个输入框。</summary>
    /// <param name="title">标题。</param>
    /// <param name="prompt">说明这一行改了会影响什么。</param>
    /// <param name="initial">现值，会全选上方便直接覆盖。</param>
    /// <param name="validate">校验：返回 null 通过，否则是提示文案；不通过时「确定」置灰。</param>
    /// <returns>确定时返回去过首尾空格的文本，取消返回 null。</returns>
    public static string? Ask(
        string title,
        string prompt,
        string initial,
        Func<string, string?>? validate = null)
    {
        var window = new TextInputWindow();

        window.TitleText.Text = title;
        window.PromptText.Text = prompt;
        window.InputBox.Text = initial;
        window._validate = validate ?? (_ => null);
        window.RefreshState();

        window.InputBox.TextChanged += (_, _) => window.RefreshState();
        window.InputBox.Focus();
        window.InputBox.SelectAll();

        return window.ShowDialog() == true ? window.InputBox.Text.Trim() : null;
    }

    private void RefreshState()
    {
        var error = _validate(InputBox.Text);
        ErrorText.Text = error ?? string.Empty;
        OkButton.IsEnabled = error is null;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
