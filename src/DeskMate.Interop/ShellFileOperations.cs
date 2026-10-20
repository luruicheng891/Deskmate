using System.IO;
using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 走 shell 的文件操作。目前只有「移到回收站」这一种。
/// <para>
/// 为什么不用 <c>File.Delete</c>：那是不给回头的删除，用户手滑一次东西就没了。
/// <c>SHFileOperation</c> 带上 <c>FOF_ALLOWUNDO</c> 才是资源管理器那个「删到回收站」，
/// 而且文件正被占用、权限不足这些情况它会照实返回错误码，不会静默地做到一半。
/// </para>
/// </summary>
public static class ShellFileOperations
{
    /// <summary>FO_DELETE。</summary>
    private const uint FoDelete = 0x0003;

    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;

    /// <summary>用户取消（我们屏蔽了 shell 自己的界面，正常拿不到这一码，留着是为了不把它当成失败）。</summary>
    private const int ErrorCancelled = 1223;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Shfileopstruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref Shfileopstruct op);

    /// <summary>
    /// 把一项移到回收站。<c>false</c> 表示被取消（什么都没动）；真失败抛 <see cref="IOException"/>。
    /// </summary>
    public static bool Recycle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。", nameof(path));
        }

        var op = new Shfileopstruct
        {
            hwnd = IntPtr.Zero,
            wFunc = FoDelete,

            // pFrom 是「双零结尾」的路径串： marshaller 只会补一个 \0，所以这里自己再带两个。
            pFrom = path.Trim().Trim('"') + "\0\0",
            fFlags = (ushort)(FofAllowUndo | FofSilent | FofNoConfirmation | FofNoErrorUi),
        };

        var code = SHFileOperationW(ref op);

        if (code == ErrorCancelled || op.fAnyOperationsAborted)
        {
            return false;
        }

        if (code != 0)
        {
            throw new IOException($"移到回收站没有成功（shell 返回 {code}）。文件可能正被别的程序占用。");
        }

        return true;
    }
}
