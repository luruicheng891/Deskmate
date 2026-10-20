using System.Security;
using System.Windows;
using DeskMate.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeskMate.App.Services;

/// <summary>
/// 换肤：把 App.xaml 里 0 号合并字典换成 Light/Dark。
/// 所有控件的颜色都用 DynamicResource 引用色板键名，因此换字典即刻全局生效，无需重建窗口。
/// </summary>
public sealed class ThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Uri LightUri = new("/Resources/Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("/Resources/Themes/Dark.xaml", UriKind.Relative);

    private readonly IConfigService _config;
    private readonly ILogger<ThemeService> _logger;
    private string _appliedUri = string.Empty;

    /// <summary>构造主题服务。</summary>
    public ThemeService(IConfigService config, ILogger<ThemeService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>按当前配置套用主题。</summary>
    public void ApplyCurrent() => Apply(_config.Current.Theme);

    /// <summary>套用指定主题。</summary>
    /// <param name="theme">Light / Dark / System。</param>
    public void Apply(string theme)
    {
        var useDark = theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => IsSystemDark(),
        };

        var target = useDark ? DarkUri : LightUri;
        if (string.Equals(_appliedUri, target.OriginalString, StringComparison.Ordinal))
        {
            return;
        }

        var merged = Application.Current.Resources.MergedDictionaries;

        // 0 号位置固定留给主题色板（App.xaml 中的顺序保证了这一点），Common.xaml 紧随其后。
        if (merged.Count == 0)
        {
            merged.Add(new ResourceDictionary { Source = target });
        }
        else
        {
            merged[0].Source = target;
        }

        _appliedUri = target.OriginalString;
        _logger.LogInformation("主题已切换为 {Theme}", useDark ? "Dark" : "Light");
    }

    // 只读不改：系统主题状态由 Windows 维护，我们只是跟随。
    private bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is SecurityException or IOException)
        {
            _logger.LogWarning(ex, "读取系统主题失败，按亮色处理");
            return false;
        }
    }
}
