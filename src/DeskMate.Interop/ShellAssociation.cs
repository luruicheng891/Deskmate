using System.Runtime.InteropServices;
using System.Text;

namespace DeskMate.Interop;

/// <summary>
/// 问 shell「这类文件在这台机器上到底交给谁」。
/// <para>
/// 为什么要自己问：<c>Process.Start(UseShellExecute)</c> 对「没有关联程序」的扩展名<strong>不报错</strong> ——
/// Windows 会把 <c>OpenWith.exe</c>（那个「你要用什么打开」的面板）当成关联程序起起来，返回值一切正常。
/// 于是上层看到的是「启动成功」，用户看到的是「双击了没反应」。要分清真打开和没打开，只能自己查。
/// </para>
/// <para>
/// 也不能用 <c>assoc .md</c> 那种查法：本机 <c>HKCR\.md</c> 的默认值指向一个只有图标的 ProgID，
/// 真正能用的是当前用户的 UserChoice（<c>Typora.md</c>）。<c>AssocQueryString</c> 走的就是
/// 合并了 UserChoice 的那套解析，和 ShellExecute 实际会用的那条链一致。
/// </para>
/// </summary>
public static class ShellAssociation
{
    /// <summary>ASSOCSTR_COMMAND：完整的命令行模板（<c>"C:\x.exe" "%1"</c>）。</summary>
    private const int AssocStrCommand = 1;

    /// <summary>ASSOCSTR_EXECUTABLE：只要那个 exe 的路径。</summary>
    private const int AssocStrExecutable = 2;

    /// <summary>ASSOCSTR_DELEGATEEXEC：委托给别的处理程序时，那个处理程序的 exe。</summary>
    private const int AssocStrDelegateExec = 16;

    private const int Ok = 0;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? buffer, ref int length);

    /// <summary>
    /// 这个扩展名真正会落到的那个 exe（完整路径）。返回 null 表示这台机器上没给它配默认程序 ——
    /// 包括「只配到了系统的 OpenWith 面板」这一种，那不算有人能打开它。
    /// </summary>
    public static string? ExecutableFor(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        var executable = Query(AssocStrExecutable, extension);
        var command = executable is null ? Query(AssocStrDelegateExec, extension) ?? Query(AssocStrCommand, extension) : null;
        var resolved = executable ?? FirstTokenOf(command);

        if (resolved is null || resolved.Length == 0)
        {
            return null;
        }

        // 「打开方式」面板不是能看这个文件的程序，落到它身上就等于没有关联。
        return resolved.Contains("OpenWith.exe", StringComparison.OrdinalIgnoreCase) ? null : resolved;
    }

    private static string? Query(int kind, string extension)
    {
        var buffer = new StringBuilder(1024);
        var length = buffer.Capacity;

        return AssocQueryString(0, kind, extension, null, buffer, ref length) == Ok && buffer.Length > 0
            ? buffer.ToString()
            : null;
    }

    private static string? FirstTokenOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }
}
