using NETDock.Core;
using Xunit;

namespace NETDock.Tests;

public class StoreAppLogoTests
{
    [Theory]
    [InlineData(@"shell:AppsFolder\Exora_xhzcyxfzt9y0m!App", "Exora_xhzcyxfzt9y0m", "App")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe", "App")]
    public void Aumids_split(string target, string family, string app) =>
        Assert.Equal((family, app), StoreAppLogo.SplitAumid(target));

    [Theory]
    [InlineData(@"shell:AppsFolder\{6D809377-6AF0-444B-8957-A3773F02200E}\App\app.exe")] // desktop app in the Apps folder
    [InlineData(@"C:\Tools\app.exe")]
    [InlineData(@"shell:AppsFolder\NoBang")]
    public void Non_store_targets_have_no_aumid(string target) => Assert.Null(StoreAppLogo.SplitAumid(target));

    [Fact]
    public void Manifest_names_the_apps_logo()
    {
        const string xml = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Applications>
                <Application Id="CalendarApp"><uap:VisualElements Square44x44Logo="Assets\Cal\Square44x44Logo.png" Square150x150Logo="Assets\Cal\Square150x150Logo.png" /></Application>
                <Application Id="App"><uap:VisualElements Square44x44Logo="Assets\Mail\Square44x44Logo.png" Square150x150Logo="Assets\Mail\Square150x150Logo.png" /></Application>
              </Applications>
            </Package>
            """;
        Assert.Equal([@"Assets\Mail\Square44x44Logo.png", @"Assets\Mail\Square150x150Logo.png"], StoreAppLogo.LogoNames(xml, "App"));
        Assert.Empty(StoreAppLogo.LogoNames("not xml", "App"));
    }

    private static readonly string[] ExoraFiles =
    [
        @"X\Square44x44Logo.scale-100.png", @"X\Square44x44Logo.scale-400.png",
        @"X\Square44x44Logo.targetsize-48.png", @"X\Square44x44Logo.targetsize-256.png",
        @"X\Square44x44Logo.altform-unplated_targetsize-48.png", @"X\Square44x44Logo.altform-unplated_targetsize-256.png",
        @"X\Square44x44Logo.altform-lightunplated_targetsize-256.png",
        @"X\Square150x150Logo.scale-400.png", @"X\StoreLogo.scale-400.png",
    ];

    [Theory]
    [InlineData(128, @"X\Square44x44Logo.altform-unplated_targetsize-256.png")]
    [InlineData(48, @"X\Square44x44Logo.altform-unplated_targetsize-48.png")]
    [InlineData(512, @"X\Square44x44Logo.altform-unplated_targetsize-256.png")] // nothing that big: the largest
    public void Picks_the_unplated_logo_big_enough(int size, string expected) =>
        Assert.Equal(expected, StoreAppLogo.Pick(@"Assets\X\Square44x44Logo.png", ExoraFiles, size));

    [Fact]
    public void Scale_only_logos_are_sized_by_scale() =>
        Assert.Equal(@"X\Square44x44Logo.scale-400.png", StoreAppLogo.Pick("Square44x44Logo.png", [@"X\Square44x44Logo.scale-100.png", @"X\Square44x44Logo.scale-400.png"], 128));
}
