using DeskMate.Core.Models;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Xunit;

namespace DeskMate.Tests;

public class HotkeyManagerTests
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    [Theory]
    [InlineData("Alt+Space", ModAlt, 0x20)]
    [InlineData("alt+space", ModAlt, 0x20)]
    [InlineData("Ctrl+Shift+D", ModControl | ModShift, 'D')]
    [InlineData("Ctrl+Alt+1", ModControl | ModAlt, '1')]
    [InlineData("Win+F12", ModWin, 0x7B)]
    [InlineData("Ctrl+Enter", ModControl, 0x0D)]
    [InlineData("  Ctrl  +  F5  ", ModControl, 0x74)]
    public void TryParse_AcceptsCommonForms(string hotkey, uint expectedModifiers, uint expectedVk)
    {
        Assert.True(HotkeyManager.TryParse(hotkey, out var modifiers, out var vk));
        Assert.Equal(expectedModifiers, modifiers);
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("J")]
    [InlineData("Alt+Space+Enter")]
    [InlineData("Alt+中文")]
    [InlineData("Ctrl+@")]
    public void TryParse_RejectsInvalidForms(string? hotkey)
        => Assert.False(HotkeyManager.TryParse(hotkey, out _, out _));

    [Fact]
    public void TryParse_RequiresAtLeastOneModifier()
        => Assert.False(HotkeyManager.TryParse("A", out _, out _));
}
