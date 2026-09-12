using System;
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Where DLSS's feature libraries are looked for.
///
/// They are NVIDIA redistributables, so they are never in this repository and
/// never in the game's assets: they ship beside the client, next to
/// <c>Optimum.Render.Vulkan.dll</c> and the Silk.NET natives, or in a
/// <c>dlss</c> folder there. A deployed client has no
/// <c>OPTIMUM_NGX_FEATURE_PATH</c> set and no repository checkout above it, so
/// the application directory has to be one of the searched candidates or the
/// upscaler is unreachable outside a development tree - which is how it failed
/// before 2026-09-12.
///
/// No driver, no device and no NGX here: this is a pure path question.
/// </summary>
public class NgxFeaturePathTests
{
    [Fact]
    public void TheApplicationDirectoryAndItsDlssFolderAreSearched()
    {
        IReadOnlyList<string> candidates = NgxSession.FeaturePathCandidates();

        string application = AppContext.BaseDirectory;
        Assert.Contains(application, candidates);
        Assert.Contains(Path.Combine(application, "dlss"), candidates);
    }

    /// <summary>
    /// Order matters twice: the explicit override wins over everything (that is
    /// what a test run and a bisect use it for), and the shipping layout is
    /// searched before the development vendor tree, so a deployed client never
    /// picks up a stale library from a checkout that happens to be above it.
    /// </summary>
    [Fact]
    public void TheOverrideComesFirstAndTheShippingLayoutBeatsTheVendorTree()
    {
        string? before = Environment.GetEnvironmentVariable(NgxSession.FeaturePathVariable);
        try
        {
            string configured = Path.Combine(Path.GetTempPath(), "optimum-ngx-feature-path-test");
            Environment.SetEnvironmentVariable(NgxSession.FeaturePathVariable, configured);

            IReadOnlyList<string> candidates = NgxSession.FeaturePathCandidates();
            Assert.Equal(configured, candidates[0]);
            Assert.Equal(AppContext.BaseDirectory, candidates[1]);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "dlss"), candidates[2]);

            int firstVendor = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Contains(Path.Combine("vendor", "dlss"), StringComparison.Ordinal))
                {
                    firstVendor = i;
                    break;
                }
            }
            Assert.True(firstVendor > 2, "the vendor development tree must be searched after the shipping layout");
        }
        finally
        {
            Environment.SetEnvironmentVariable(NgxSession.FeaturePathVariable, before);
        }
    }

    /// <summary>
    /// A candidate only becomes a search path when it exists and really holds
    /// feature libraries, so an empty application directory (every CI machine)
    /// does not hand NGX a path list full of nothing.
    /// </summary>
    [Fact]
    public void OnlyDirectoriesThatHoldFeatureLibrariesBecomeSearchPaths()
    {
        foreach (string path in NgxSession.FindFeaturePaths())
        {
            Assert.True(Directory.Exists(path));
            Assert.NotEmpty(Directory.GetFiles(path, "libnvidia-ngx-*.so*"));
        }
    }
}
