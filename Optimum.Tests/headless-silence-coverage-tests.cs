using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The headless render harness runs in the user's own session with no window. It
/// must not play sound at them either: the mixer is created muted and every later
/// attempt to restore the volume is answered with silence, while the persisted
/// sound settings are never touched.
/// </summary>
public class HeadlessSilenceCoverageTests
{
    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    [Fact]
    public void TheMixerIsCreatedMutedWhileHeadless()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        int start = platform.IndexOf("public void StartAudio()", StringComparison.Ordinal);
        Assert.True(start > 0, "StartAudio must exist");
        string body = platform.Substring(start, 700);
        int created = body.IndexOf("audio = new AudioOpenAl(logger);", StringComparison.Ordinal);
        int muted = body.IndexOf("audio.MasterSoundLevel = 0f;", StringComparison.Ordinal);
        Assert.True(created > 0 && muted > created,
            "the mixer must be created and then muted while the headless harness runs");
        Assert.Contains("Vintagestory.API.Config.OptimumHeadless.Enabled", body);
    }

    [Fact]
    public void RestoringTheVolumeIsAnsweredWithSilenceWhileHeadless()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains(
            "audio.MasterSoundLevel = (Vintagestory.API.Config.OptimumHeadless.Enabled ? 0f : value);",
            platform);
    }

    [Fact]
    public void ThePersistedSoundSettingsAreNeverWritten()
    {
        // Only the running mixer is silenced: nothing in the headless path may assign
        // ClientSettings' sound levels, or a capture would change what the user hears
        // the next time they play.
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        int start = platform.IndexOf("public void StartAudio()", StringComparison.Ordinal);
        string body = platform.Substring(start, 700);
        Assert.DoesNotContain("ClientSettings.MasterSoundLevel =", body);
        Assert.DoesNotContain("ClientSettings.SoundLevel =", body);
    }

    [Fact]
    public void ThePatcherCarriesBothBodies()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"StartAudio\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"set_MasterSoundLevel\", 1", patcher);
    }
}
