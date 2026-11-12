using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Optimum.Tests;

public sealed class CairoDebugStartupTests
{
    // A reference built through the solution lands in bin/Debug even when the tests are Release, so look for the
    // configuration the tests run in first and then the other one.
    private static string FindFixture()
    {
        string running = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar) ? "Release" : "Debug";
        string other = running == "Release" ? "Debug" : "Release";
        try
        {
            return PatchReader.FindRepositoryFile($"tools/Cairo.AbiFixture/bin/{running}/net10.0/Cairo.AbiFixture.dll");
        }
        catch (FileNotFoundException)
        {
            return PatchReader.FindRepositoryFile($"tools/Cairo.AbiFixture/bin/{other}/net10.0/Cairo.AbiFixture.dll");
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("trace", true)]
    public async Task FreshProcessRespectsEnvironmentAndPublicEnableField(string? setting, bool expected)
    {
        string fixture = FindFixture();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(fixture); start.ArgumentList.Add("--debug-state");
        if (setting == null) start.Environment.Remove("CAIRO_DEBUG_DISPOSE");
        else start.Environment["CAIRO_DEBUG_DISPOSE"] = setting;
        using var process = Process.Start(start);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Cairo startup fixture timed out."); }
        string text = await output;
        Assert.True(process.ExitCode == 0, text + await errors);
        using var state = JsonDocument.Parse(text);
        Assert.Equal(expected, state.RootElement.GetProperty("initial").GetBoolean());
        Assert.Equal(setting, state.RootElement.GetProperty("before").GetString());
        Assert.Equal(setting, state.RootElement.GetProperty("after").GetString());
    }
}
