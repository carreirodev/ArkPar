// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Tests.App;

/// <summary>
/// The WPC Solutions logos at the end of Settings. WinUI draws an SVG through Direct2D, which ignores a &lt;style&gt;
/// block: a logo whose colours sit in CSS classes comes out black. app/build-logo.ps1 moves them onto each shape.
/// </summary>
[TestClass]
public sealed class AppLogoTests
{
    [TestMethod]
    [DataRow("wpc-logo-light-background.svg")]
    [DataRow("wpc-logo-dark-background.svg")]
    public void CarriesItsColoursOnEachShape(string name)
    {
        var logo = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", name));

        Assert.DoesNotContain("<style", logo, "a <style> block, which Direct2D ignores");
        Assert.DoesNotContain("class=", logo, "a CSS class, which Direct2D ignores");
        Assert.DoesNotContain("<text", logo, "text, which Direct2D draws in whatever font this PC has");
        Assert.Contains("fill=\"#ef372a\"", logo, "the lamp's red");
    }
}
