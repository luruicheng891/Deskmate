using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 启动台噪声过滤规则。既测「该拦的拦住」，也测「不该误杀的留下」——
/// 后者更重要：把用户真正要用的应用藏掉，比多显示几个系统工具严重得多。
/// </summary>
public class AppFilterRulesTests
{
    private static AppInfo App(string name, string target)
        => new() { Id = target, Name = name, Path = target, Source = "StartMenu" };

    [Theory]
    [InlineData("Office 语言首选项", @"C:\PROGRAM FILES\MICROSOFT OFFICE\ROOT\OFFICE16\APPWZ.CPL")]
    [InlineData("组件服务", @"C:\WINDOWS\SYSTEM32\COMEXP.MSC")]
    [InlineData("ODBC 数据源(64 位)", @"C:\WINDOWS\SYSTEM32\ODBCINT.DLL")]
    [InlineData("SDXHelper", @"C:\PROGRAM FILES\MICROSOFT OFFICE\ROOT\OFFICE16\SDXHELPER.EXE")]
    [InlineData("SKYPESERVER", @"C:\PROGRAM FILES\MICROSOFT OFFICE\ROOT\OFFICE16\SKYPESRV\SKYPESERVER.EXE")]
    [InlineData("磁盘碎片整理程序", @"C:\WINDOWS\SYSTEM32\DFRGUI.EXE")]
    [InlineData("GetHelp", @"C:\PROGRAM FILES\WINDOWSAPPS\MICROSOFT.GETHELP\GETHELP.EXE")]
    [InlineData("Character Map", @"C:\WINDOWS\SYSTEM32\CHARMAP.EXE")]
    [InlineData("Task Scheduler", @"C:\WINDOWS\SYSTEM32\MMC.EXE")]
    [InlineData("Performance Monitor", @"C:\WINDOWS\SYSTEM32\PERFMON.EXE")]
    [InlineData("Windows App 认证工具包", @"C:\PROGRAM FILES\WINDOWSAPPS\WACK\CERTIFICATION.exe")]
    public void IsNoise_BlocksSystemComponents(string name, string target)
        => Assert.True(AppFilterRules.IsNoise(App(name, target)));

    [Theory]
    [InlineData("Microsoft Access", @"C:\PROGRAM FILES\MICROSOFT OFFICE\ROOT\OFFICE16\MSACCESS.EXE")]
    [InlineData("Word", @"C:\PROGRAM FILES\MICROSOFT OFFICE\ROOT\OFFICE16\WINWORD.EXE")]
    [InlineData("Excel", @"C:\PROGRAM FILES\MICROSOFT OFFICE\ROOT\OFFICE16\EXCEL.EXE")]
    [InlineData("MySQL Server 8.0", @"C:\PROGRAM FILES\MYSQL\MYSQL SERVER 8.0\BIN\MYSQLD.EXE")]
    [InlineData("Google Chrome", @"C:\PROGRAM FILES\GOOGLE\CHROME\APPLICATION\CHROME.EXE")]
    [InlineData("记事本", @"C:\WINDOWS\SYSTEM32\NOTEPAD.EXE")]
    [InlineData("注册表编辑器", @"C:\WINDOWS\REGEDIT.EXE")]
    [InlineData("Epic Games Launcher", @"C:\PROGRAM FILES (X86)\EPIC GAMES\LAUNCHER\EPICGAMESLAUNCHER.EXE")]
    [InlineData("Keil uVision5", @"D:\KEIL_V5\UV4\UV4.EXE")]
    [InlineData("STM32CubeMX", @"D:\STM32CUBEMX\STM32CUBEMX.EXE")]
    [InlineData("Visual Studio Code", @"C:\PROGRAM FILES\MICROSOFT VS CODE\CODE.EXE")]
    [InlineData("微信", @"D:\WECHAT\WECHAT.EXE")]
    [InlineData("Steam", @"C:\PROGRAM FILES (X86)\STEAM\STEAM.EXE")]
    [InlineData("Python 3.12", @"C:\PYTHON312\PYTHON.EXE")]
    public void IsNoise_KeepsRealApplications(string name, string target)
        => Assert.False(AppFilterRules.IsNoise(App(name, target)));

    [Fact]
    public void IsNoise_BlocksEntriesWithoutName()
        => Assert.True(AppFilterRules.IsNoise(new AppInfo { Id = @"C:\A\B.EXE", Name = "  ", Path = @"C:\A\B.EXE" }));
}
