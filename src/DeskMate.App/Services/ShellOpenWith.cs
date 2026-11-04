using System.Diagnostics;

namespace DeskMate.App.Services;

/// <summary>
/// 把系统的「打开方式」面板叫到某个文件上（<c>shell32.dll,OpenAs_RunDLL</c>）。
/// <para>
/// 走 rundll32 而不是自己列一遍候选程序：系统那份列表包含了用户最近的挑选、Store 应用与「更多选项」，
/// 自己拼一份等于把它复刻一遍还必然复刻歪。整个过程只是拉起面板，DeskMate 不改动任何关联设置。
/// </para>
/// </summary>
public static class ShellOpenWith
{
    /// <summary>为这个文件弹出系统的「打开方式」选择面板。</summary>
    /// <exception cref="Exception">面板拉不起来（权限、路径已不存在）时由调用方给用户解释。</exception>
    public static void Show(string path)
    {
        // 用 ArgumentList 而不是拼一整个字符串：路径里带空格与引号时，拼接会把参数切错。
        var startInfo = new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            throw new InvalidOperationException("系统没接这个请求。");
        }
    }
}
