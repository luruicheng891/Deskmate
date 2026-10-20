using System.Runtime.InteropServices;
using System.Text;

namespace DeskMate.Interop;

/// <summary>
/// 解析 .lnk 快捷方式（COM 互操作），拿到真实目标路径、启动参数与图标位置。
/// Dock 与启动台拿到的常常是快捷方式而非 exe，不解折就取不到图标、也无法判断进程是否在运行。
/// </summary>
public static class ShellLink
{
    private const int MaxPath = 1024;
    private const uint StgmRead = 0;
    private const uint SlrNoUi = 0x0001;
    private const uint SlrUpdate = 0x0004;

    /// <summary>快捷方式解析结果。</summary>
    /// <param name="Target">目标 exe / 文档绝对路径，解析失败为空串。</param>
    /// <param name="Arguments">附加启动参数。</param>
    /// <param name="IconPath">自定义图标文件路径，未自定义时为空串。</param>
    /// <param name="IconIndex">自定义图标在文件中的序号，负数表示取文件末尾图标。</param>
    public readonly record struct ResolveResult(string Target, string Arguments, string IconPath, int IconIndex);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkWClass;

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // 下列顺序必须与 shellapi.h 中 IShellLink 的 vtable 完全一致，
        // 即便用不到的方法也要占位声明，否则后面的槽位会错位崩溃。
        void GetPath(StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(StringBuilder pszName, int cchMaxName);

        void SetDescription(string pszName);

        void GetWorkingDirectory(StringBuilder pszDir, int cchMaxPath);

        void SetWorkingDirectory(string pszDir);

        void GetArguments(StringBuilder pszArgs, int cchMaxPath);

        void SetArguments(string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(StringBuilder pszIconPath, int cchIconPath, out int piIcon);

        void SetIconLocation(string pszIconPath, int iIcon);

        void SetRelativePath(string pszPathRel, int dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath(string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        void IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>按扩展名判断是否为快捷方式。</summary>
    public static bool IsShortcut(string path)
        => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析快捷方式。任何一步失败都返回空结果而不抛异常——
    /// 扫描开始菜单时会遇到大量权限受限或已失效的 .lnk，一个坏链接不该中断整轮扫描。
    /// </summary>
    public static ResolveResult Resolve(string lnkPath)
    {
        if (string.IsNullOrWhiteSpace(lnkPath))
        {
            return default;
        }

        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkWClass();
            ((IPersistFile)link).Load(lnkPath, StgmRead);

            // Resolve 会顺带修复相对路径与失效目标；目标已删除时会失败，但 GetPath 仍能读到记录的原始路径。
            try
            {
                link.Resolve(IntPtr.Zero, SlrNoUi | SlrUpdate);
            }
            catch (COMException)
            {
                // 忽略：继续用存储值。
            }

            var target = new StringBuilder(MaxPath);
            link.GetPath(target, MaxPath, IntPtr.Zero, SlrNoUi);

            var arguments = new StringBuilder(MaxPath);
            link.GetArguments(arguments, MaxPath);

            var iconPath = new StringBuilder(MaxPath);
            link.GetIconLocation(iconPath, MaxPath, out var iconIndex);

            return new ResolveResult(target.ToString(), arguments.ToString(), iconPath.ToString(), iconIndex);
        }
        catch (COMException)
        {
            return default;
        }
        catch (InvalidCastException)
        {
            return default;
        }
        finally
        {
            // 显式断开 RCW，避免大量 .lnk 扫描时 COM 对象等到最终终结器才回收。
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    /// <summary>只要目标路径的便捷重载，失败返回空串。</summary>
    public static string ResolveTarget(string lnkPath) => Resolve(lnkPath).Target;

    /// <summary>
    /// 新建一个 .lnk 快捷方式。
    /// 启动台的「手动添加应用」用它：往快捷方式文件夹里放一个指向目标程序的入口，
    /// 而不是复制程序本体，也绝不改动用户原来的文件。
    /// </summary>
    /// <param name="linkPath">要创建的 .lnk 完整路径。</param>
    /// <param name="targetPath">目标程序（或文档）完整路径。</param>
    /// <param name="description">备注名，会作为快捷方式显示名的一部分，可为空。</param>
    /// <param name="workingDirectory">起始位置，可为空（留空时 Shell 用目标所在目录）。</param>
    /// <param name="arguments">启动参数，可为空。</param>
    /// <param name="iconLocation">图标位置原始串（如 <c>%windir%\x.dll,-50</c>），可为空。</param>
    /// <param name="iconIndex">图标序号，配合 <paramref name="iconLocation"/> 使用。</param>
    public static void Create(
        string linkPath,
        string targetPath,
        string? description = null,
        string? workingDirectory = null,
        string? arguments = null,
        string? iconLocation = null,
        int iconIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("快捷方式路径与目标路径都不能为空。");
        }

        var link = (IShellLinkW)new ShellLinkWClass();
        try
        {
            link.SetPath(targetPath);

            if (!string.IsNullOrWhiteSpace(description))
            {
                link.SetDescription(description);
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                link.SetWorkingDirectory(workingDirectory);
            }

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                link.SetArguments(arguments);
            }

            if (!string.IsNullOrWhiteSpace(iconLocation))
            {
                link.SetIconLocation(iconLocation, iconIndex);
            }

            ((IPersistFile)link).Save(linkPath, fRemember: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }
}
