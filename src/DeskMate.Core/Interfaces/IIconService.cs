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

    /// <summary>清空缓存（改主题或图标大小后调用）。</summary>
    void ClearCache();
}
