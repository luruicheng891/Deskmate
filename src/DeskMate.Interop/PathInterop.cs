using System.Runtime.InteropServices;
using System.Text;

namespace DeskMate.Interop;

/// <summary>
/// 路径形态修正。快捷方式里的目标经常被写成 8.3 短路径（例如 <c>QODERC~1.EXE</c>），
/// 短路径拿去当进程名比对进程列表是永远对不上的，得先还原成长路径。
/// </summary>
public static class PathInterop
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder? lpszShortPath, uint cchBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder? lpszLongPath, uint cchBuffer);

    /// <summary>把 8.3 短路径还原成长路径。路径不存在、或本来就已经是长路径时原样返回。</summary>
    public static string GetLongPath(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        var needed = GetLongPathNameW(path, null, 0);
        if (needed == 0)
        {
            // 调用失败多半就是「这东西不存在」，此时不该由我们改动用户给的路径。
            return path;
        }

        var buffer = new StringBuilder((int)needed);
        return GetLongPathNameW(path, buffer, (uint)buffer.Capacity) == 0 ? path : buffer.ToString();
    }

    /// <summary>取 8.3 短路径；卷上没启用短文件名时返回原路径。</summary>
    public static string GetShortPath(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        var needed = GetShortPathNameW(path, null, 0);
        if (needed == 0)
        {
            return path;
        }

        var buffer = new StringBuilder((int)needed);
        return GetShortPathNameW(path, buffer, (uint)buffer.Capacity) == 0 ? path : buffer.ToString();
    }
}
