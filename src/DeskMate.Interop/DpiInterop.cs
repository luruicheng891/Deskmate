using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 系统 DPI 缩放倍数。取图标必须按物理像素来要：按 DIP 要 30px 而屏幕是 150%，
/// 拿回来的图只有 30px，显示时却是 45px，放大后就是用户说的「图标发虚」。
/// </summary>
public static class DpiInterop
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>系统缩放倍数（100% → 1.0，150% → 1.5）。API 返回 0 时按 1.0 处理。</summary>
    public static double SystemScale { get; } = Read();

    private static double Read()
    {
        var dpi = GetDpiForSystem();
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }
}
