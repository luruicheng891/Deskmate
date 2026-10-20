using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

public class UsageServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static UsageService Create(string path)
        => new(NullLogger<UsageService>.Instance, path);

    [Fact]
    public void GetCount_IsZero_ForUnknownId()
    {
        using var service = Create(_temp.File("usage.json"));

        Assert.Equal(0, service.GetCount("never-used"));
    }

    [Fact]
    public async Task Record_SurvivesRestart()
    {
        var path = _temp.File("usage.json");

        using (var first = Create(path))
        {
            first.Record("app-a");
            first.Record("app-a");
            first.Record("app-b");
            Assert.Equal(2, first.GetCount("app-a"));
        }

        var second = Create(path);
        await second.LoadAsync();

        Assert.Equal(2, second.GetCount("app-a"));
        Assert.Equal(1, second.GetCount("app-b"));
        second.Dispose();
    }

    [Fact]
    public void Record_IgnoresBlankId()
    {
        using var service = Create(_temp.File("usage.json"));

        service.Record(string.Empty);

        Assert.Equal(0, service.GetCount(string.Empty));
    }

    public void Dispose() => _temp.Dispose();
}
