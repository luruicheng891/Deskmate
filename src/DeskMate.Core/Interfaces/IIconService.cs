using System.Windows.Media.Imaging;

namespace DeskMate.Core.Interfaces;

/// <summary>图标提取与内存缓存。</summary>
public interface IIconService
{
    /// <summary>
    /// 取图标。<c>.lnk</c> 会先解析出真实目标（或其自定义图标位置）再提取。
    /// </summary>
    /// <param name="path">exe / lnk / ico 路径。</param>
    /// <param name="size">期望边长（DIP）。</param>
    /// <returns>已 Freeze 的 BitmapSource，可跨线程使用；取不到时返回 null 由 UI 画占位图。</returns>
    Task<BitmapSource?> GetIconAsync(string path, int size = 64);

    /// <summary>
    /// 取「这一类条目在 Windows 里的默认图标」：资源管理器对同一种扩展名显示的就是它
    /// （目录给文件夹图标）。与 <see cref="GetIconAsync"/> 的区别是缓存键按类型而不是按文件 ——
    /// 一屏几百个同类型文件只问一次 Shell。
    /// </summary>
    /// <param name="path">真实路径（必须存在，Shell 靠它认类型）。</param>
    /// <param name="isDirectory">目录走文件夹图标，与扩展名无关。</param>
    /// <param name="size">期望边长（DIP）。</param>
    Task<BitmapSource?> GetTypeIconAsync(string path, bool isDirectory, int size = 32);

    /// <summary>清空缓存（改主题或图标大小后调用）。</summary>
    void ClearCache();
}
