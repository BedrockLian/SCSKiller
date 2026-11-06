using SCSKiller.Core.App;
using Xunit;

namespace SCSKiller.Tests;

public sealed class HoyoPackageTests
{
    [Fact]
    public void Package_marker_selects_the_same_isolated_data_for_GUI_and_CLI()
    {
        using var fixture = new GenshinReaderTests.Install();
        string package = Path.Combine(fixture.Root, "portable package");
        string cli = Path.Combine(package, "cli");
        Directory.CreateDirectory(cli);

        Assert.Null(AppStore.HoyoPackageData(package));
        Assert.Null(AppStore.HoyoPackageData(cli));
        File.WriteAllText(Path.Combine(package, ".scskiller-hoyo-portable"), "");
        Assert.Equal(Path.Combine(package, "data"), AppStore.HoyoPackageData(package));
        Assert.Equal(Path.Combine(package, "data"), AppStore.HoyoPackageData(cli));
        Assert.Null(AppStore.HoyoPackageData(Path.Combine(package, "bin")));
        Assert.False(Directory.Exists(Path.Combine(package, "data")));
    }
}
