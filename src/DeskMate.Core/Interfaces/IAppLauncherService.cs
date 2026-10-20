namespace DeskMate.Core.Interfaces;

/// <summary>启动应用与打开资源管理器。</summary>
public interface IAppLauncherService
{
    /// <summary>
    /// 以 ShellExecute 打开 exe / lnk / url，也可以是普通文件或目录：
    /// 文件走系统关联程序，目录直接进资源管理器。.lnk 里记录的参数与起始位置会自动带上。
    /// 失败抛出异常由调用方提示用户。
    /// </summary>
    void Launch(string path);

    /// <summary>在资源管理器中打开目录；传入文件时只是选中该文件（要直接打开文件请用 <see cref="Launch"/>）。</summary>
    void OpenInExplorer(string path);

    /// <summary>
    /// 把一个 URI 交给系统决定谁来接（<c>search-ms:</c> 这类协议就是这么用的）。
    /// 与 <see cref="Launch"/> 的区别是不要求它真的存在于磁盘上。
    /// </summary>
    void OpenUri(string uri);

    /// <summary>
    /// 这台机器上有没有给这个路径配一个<strong>真能打开它</strong>的默认程序。
    /// <para>
    /// 目录一律算有（资源管理器自己会开）。文件按扩展名问 shell；只兜到系统的「打开方式」面板时返回 false ——
    /// 那种时候交给 shell 只会弹一个选程序的对话框，用户读到的还是「双击没反应」。
    /// </para>
    /// </summary>
    bool HasDefaultProgram(string path);
}
