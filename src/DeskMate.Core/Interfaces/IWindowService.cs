using DeskMate.Core.Models;

namespace DeskMate.Core.Interfaces;

/// <summary>
/// 应用窗口的查找与切换。Dock 从「只会启动」升级成「会启动也会切回去」靠它，
/// 这也是取代任务栏的核心能力。
/// </summary>
public interface IWindowService
{
    /// <summary>取某个 exe 当前打开的可见窗口，按 Z 序自顶向下。</summary>
    Task<IReadOnlyList<AppWindowInfo>> GetWindowsAsync(string executablePath);

    /// <summary>
    /// 按<b>进程名</b>（不带扩展名）取可见窗口，同样按 Z 序。
    /// 启动之后等那个应用把窗口建出来要用它：那时我们只知道刚起来的进程叫什么，
    /// 不一定拿得到它的完整 exe 路径（shell 接手的情况尤其如此）。
    /// </summary>
    Task<IReadOnlyList<AppWindowInfo>> GetWindowsByProcessNameAsync(string processName);

    /// <summary>
    /// 决定点击图标时该操作哪个窗口：其中某个正在前台就选它（点击语义变成最小化），
    /// 否则选最上面那个。纯函数，便于单测。
    /// </summary>
    AppWindowInfo? PickClickTarget(IReadOnlyList<AppWindowInfo> windows, nint foregroundHandle);

    /// <summary>
    /// 激活窗口；已在前台则最小化。
    /// 返回 true 表示该应用确实有可见窗口且已处理（切换或最小化）；
    /// 返回 false 表示一个可见窗口都没有（没在运行，或缩回了托盘），调用方应当改为启动新实例。
    /// </summary>
    Task<bool> SwitchToAsync(string executablePath);

    /// <summary>
    /// 应用缩在托盘（进程在、可见窗口一个没有）时，把它的主窗口捞回来。
    /// <para>
    /// 只捞「像主窗口」的隐藏窗口（带标题栏、面积够大），系统认的 MainWindowHandle 也会补进来试；
    /// 捞回后立刻验证真的可见了才算成功 —— 捞不回来返回 false，调用方再走启动。
    /// 没有这一步的话，微信 4 这类允许多开的程序，用户每点一次图标就是第二个登录进程。
    /// </para>
    /// </summary>
    Task<bool> RestoreFromTrayAsync(string executablePath);

    /// <summary>直接激活指定窗口，用于预览卡片被点击。</summary>
    bool Activate(AppWindowInfo window);

    /// <summary>
    /// 关掉预览里某一张卡片对应的窗口（等价于用户点它的标题栏 ×，程序仍有机会弹「保存吗」）。
    /// 返回 false 表示这个窗口已经不存在了。
    /// </summary>
    bool Close(AppWindowInfo window);

    /// <summary>
    /// 关掉某个应用当前所有可关的窗口，返回投出关闭消息的窗口数。
    /// 0 表示它现在没有可见窗口（已经关了，或缩在托盘里）—— 调用方要把这句说清楚，
    /// 不能让用户点了「关闭窗口」之后什么都不发生。
    /// </summary>
    Task<int> CloseAllAsync(string executablePath);

    /// <summary>
    /// 强制结束某个应用的所有进程（整棵进程树）。不走 WM_CLOSE，程序没有机会弹「保存吗」，
    /// 未保存的数据直接丢 —— 只该用在「关闭窗口」关不掉或用户明确要杀的场合。
    /// 返回成功结束的进程数。
    /// </summary>
    Task<int> KillAsync(string executablePath);
}
