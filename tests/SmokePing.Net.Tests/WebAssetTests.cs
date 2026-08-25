using System.Text.RegularExpressions;
using Xunit;

namespace SmokePing.Net.Tests;

/// <summary>
/// The web interface has to work on a broken network.
///
/// That is not a general principle about offline-first design; it is the specific
/// situation this tool is opened in. Somebody notices the line is bad and goes looking
/// for why. If the page fetches its charting library from a CDN, that is the one
/// moment it cannot, and the graphs are blank exactly when they are wanted. The same
/// applies to an isolated network, and to the promise that a release unzips and runs.
///
/// Easy to lose to one convenient script tag, so it is pinned here.
/// </summary>
public sealed partial class WebAssetTests
{
    /// <summary>Web assets: the page loads nothing from the internet</summary>
    [Fact]
    public void WebAssets_The_Page_Loads_Nothing_From_The_Internet()
    {
        var root = FindWebRoot();
        if (root is null)
        {
            return;
        }

        var markup = File.ReadAllText(Path.Combine(root, "index.html"));

        foreach (Match match in ExternalReference().Matches(markup))
        {
            throw new VerificationException(
                $"index.html loads '{match.Groups[1].Value}' from another host. Vendor it under " +
                "wwwroot/vendor instead: this page is opened when the network is broken.");
        }
    }

    /// <summary>Web assets: every script and stylesheet it asks for is actually shipped</summary>
    [Fact]
    public void WebAssets_Every_Script_And_Stylesheet_It_Asks_For_Is_Actually_Shipped()
    {
        var root = FindWebRoot();
        if (root is null)
        {
            return;
        }

        var markup = File.ReadAllText(Path.Combine(root, "index.html"));
        var referenced = 0;

        foreach (Match match in LocalReference().Matches(markup))
        {
            var relative = match.Groups[1].Value;
            if (relative.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            referenced++;
            Verify.True(
                File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))),
                $"wwwroot/{relative} is referenced by index.html and exists");
        }

        // A vendored file that stopped being copied would leave this passing vacuously.
        Verify.True(referenced >= 3, $"index.html references its assets (found {referenced})");
    }

    /// <summary>Web assets: the charting library keeps its licence next to it</summary>
    [Fact]
    public void WebAssets_The_Charting_Library_Keeps_Its_Licence_Next_To_It()
    {
        var root = FindWebRoot();
        if (root is null)
        {
            return;
        }

        var vendor = Path.Combine(root, "vendor", "uplot");
        Verify.True(File.Exists(Path.Combine(vendor, "uPlot.iife.min.js")), "uPlot is vendored");

        // Bundling somebody else's MIT code means shipping their copyright notice.
        var licence = Path.Combine(vendor, "LICENSE");
        Verify.True(File.Exists(licence), "and its licence travels with it");
        Verify.Contains(File.ReadAllText(licence), "MIT", "which is the MIT licence");
    }

    /// <summary>Walks up from the test binary looking for the served web root.</summary>
    private static string? FindWebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SmokePing.Net", "wwwroot");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    // Scripts and stylesheets only. Navigation links are hrefs too, and a link to
    // somebody else's site is a link; a script from it is a dependency.
    /// <summary>A script or stylesheet loaded from another host.</summary>
    [GeneratedRegex(
        @"<(?:script[^>]*\ssrc|link[^>]*\shref)\s*=\s*[""'](\s*(?:https?:)?//[^""']+)[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExternalReference();

    /// <summary>A script or stylesheet loaded from a file of our own.</summary>
    [GeneratedRegex(
        @"<(?:script[^>]*\ssrc|link[^>]*\shref)\s*=\s*[""'](?!\s*(?:https?:)?//)([^""']+)[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex LocalReference();
}
