namespace DeskMate.Tests;

/// <summary>
/// 单测替身用的退出监视句柄：产品代码只管在不需要时 Dispose 它，测试里它什么都不盯。
/// 「图标什么时候熄灭」由 Dock 的测试用假监视器直接触发退出回调来验，不需要真起进程。
/// </summary>
internal sealed class NoExitWatch : IDisposable
{
    public void Dispose()
    {
    }
}
