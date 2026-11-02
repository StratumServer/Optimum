using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Optimum.Tests;

public sealed class CairoDebugStartupTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("trace", true)]
    public async Task FreshProcessRespectsEnvironmentAndPublicEnableField(string? setting, bool expected)
    {
        string configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar) ? "Release" : "Debug";
        string fixture = PatchReader.FindRepositoryFile($"tools/Cairo.AbiFixture/bin/{configuration}/net10.0/Cairo.AbiFixture.dll");
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
