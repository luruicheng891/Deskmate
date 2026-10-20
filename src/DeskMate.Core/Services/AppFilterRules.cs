using DeskMate.Core.Models;

namespace DeskMate.Core.Services;

/// <summary>
/// 启动台噪声过滤。注册表 App Paths 与 Windows 工具目录里混着大量后台组件和管理单元件
/// （Office 语言首选项、组件服务、ODBC 数据源、磁盘碎片整理、各类 helper/server），
/// 没人会从启动台点它们，只会把真正要用的应用淹掉。
/// <para>
/// 规则刻意保守：只拦「几乎不可能有人从启动台启动」的确定项。
/// 判断标准因人而异的那部分，交给用户在启动台里右键自行隐藏。
/// </para>
/// </summary>
public static class AppFilterRules
{
    /// <summary>控制面板小程序、MMC 管理单元、纯 DLL —— 它们不是应用。</summary>
    private static readonly string[] BlockedTargetExtensions =
    [
        ".cpl", ".msc", ".dll", ".ocx", ".sct", ".appref-ms", ".msu", ".cab",
    ];

    /// <summary>
    /// 名称或目标文件名包含其中任一片段即视为噪声。中英文都列出来，
    /// 因为开始菜单条目名跟随系统语言，而注册表 App Paths 的条目名永远是英文 exe 名。
    /// </summary>
    private static readonly string[] BlockedNameParts =
    [
        // 中文系统工具
        "语言首选项", "组件服务", "计划任务", "资源监视器", "性能监视器", "系统监视器",
        "磁盘清理", "碎片", "字符映射", "私有字符", "屏幕键盘", "讲述人", "语音", "放大镜",
        "数据源", "证书", "安全合规", "认证工具包", "获取帮助", "帮助和支持", "管理工具",
        "管理单元", "事件查看器", "本地安全", "恢复驱动器", "系统配置", "更新程序", "卸载",
        "部署", "监视", "检查器", "诊断", "代理", "服务",

        // 英文对应项与后台组件
        "language preferences", "component services", "task scheduler", "resource monitor",
        "performance monitor", "disk cleanup", "defrag", "charmap", "magnifier", "narrator",
        "on-screen keyboard", "speech", "certificate", "compliance", "certification",
        "get help", "gethelp", "support assist", "administrative tools", "odbc", "iscsi",
        "recovery drive",
        "system configuration", "eventvwr", "dfrgui", "wsappc", "secpol", "gpedit", "hdwwiz",
        "verifier", "helper", "wizard", "snap-in", "mmc", "unins", "uninstall", "proxy",
        "updater", "sched", "appverif", "comres", "mycomput", "skypesrv", "msoasb", "msoadfsb",
        "officebackground",
    ];

    /// <summary>是否为不该出现在启动台/搜索结果里的噪声条目。</summary>
    public static bool IsNoise(AppInfo app)
    {
        if (string.IsNullOrWhiteSpace(app.Name))
        {
            return true;
        }

        // AppInfo.Id 存的是展开后的真实目标路径，扩展名与文件名都以它为准
        // （app.Path 对开始菜单条目而言是 .lnk 本身，判断不了类型）。
        var target = app.Id;

        foreach (var part in BlockedNameParts)
        {
            if (app.Name.Contains(part, StringComparison.OrdinalIgnoreCase)
                || target.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var extension = Path.GetExtension(target);
        if (extension.Length > 0
            && BlockedTargetExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
