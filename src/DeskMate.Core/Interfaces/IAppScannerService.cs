using DeskMate.Core.Models;

namespace DeskMate.Core.Interfaces;

/// <summary>已安装应用扫描（开始菜单 + 注册表 App Paths）。</summary>
public interface IAppScannerService
{
    /// <summary>
    /// 扫描全部应用。<paramref name="forceRefresh"/> 为 false 时优先用内存/磁盘缓存。
    /// </summary>
    Task<IReadOnlyList<AppInfo>> ScanInstalledAppsAsync(bool forceRefresh = false);

    /// <summary>后台重新扫描并覆盖缓存，不阻塞调用方。</summary>
    Task<IReadOnlyList<AppInfo>> RefreshAsync();

    /// <summary>按关键词过滤已缓存应用（含拼音匹配）。</summary>
    Task<IReadOnlyList<AppInfo>> SearchAppsAsync(string keyword);

    /// <summary>
    /// 把一个程序加入启动台的快捷方式文件夹：源是 <c>.lnk</c> 时连参数与自定义图标一起转写成新快捷方式，
    /// 源是 <c>.exe</c> 时新建一个指向它的快捷方式。只往启动台文件夹写，不改动用户原文件。
    /// 添加完成后自动重扫，返回新建的 .lnk 路径。
    /// </summary>
    Task<string> AddToLaunchpadAsync(string sourcePath);
}
