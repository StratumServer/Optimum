using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 3: shader program, uniform and UBO calls are
/// ClientPlatformAbstract virtuals. ShaderProgramBase and UBO are back to the vanilla
/// shape with ScreenManager.Platform calls where the GL lines were; ClientPlatformWindows
/// holds the device branch and the GL lines as overrides. A direct device or GL call left
/// in either class would bypass whichever platform the client installed.
/// </summary>
public class PlatformProgramUboVirtualsCoverageTests
{
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string WindowsPath = "Vintagestory.Client.NoObf/ClientPlatformWindows.cs";
    private const string ProgramPath = "Vintagestory.Client.NoObf/ShaderProgramBase.cs";
    private const string UboPath = "Vintagestory.Client.NoObf/UBO.cs";

    /// <summary>The member, its parameter list, the device call and the GL call its override must hold.</summary>
    private static readonly (string Name, string Parameters, string Device, string Gl)[] Members =
    {
        ("UseShaderProgram", "int programId", "optimumDevice.UseProgram(programId);", "GL.UseProgram(programId);"),
        ("DisposeShaderProgram", "ShaderProgramBase program", "optimumDevice.DeleteProgram(program.ProgramId);", "GL.DeleteProgram(program.ProgramId);"),
        ("BindSampler", "int unit, int samplerId", "optimumDevice.BindSampler(unit, samplerId);", "GL.BindSampler(unit, samplerId);"),
        ("SetUniform", "int programId, int location, float value", "optimumDevice.SetUniform(programId, location, value);", "GL.Uniform1(location, value);"),
        ("SetUniform", "int programId, int location, int value", "optimumDevice.SetUniform(programId, location, value);", "GL.Uniform1(location, value);"),
        ("SetUniform", "int programId, int location, float x, float y", "optimumDevice.SetUniform(programId, location, x, y);", "GL.Uniform2(location, x, y);"),
        ("SetUniform", "int programId, int location, float x, float y, float z", "optimumDevice.SetUniform(programId, location, x, y, z);", "GL.Uniform3(location, x, y, z);"),
        ("SetUniform", "int programId, int location, float x, float y, float z, float w", "optimumDevice.SetUniform(programId, location, x, y, z, w);", "GL.Uniform4(location, x, y, z, w);"),
        ("SetUniform", "int programId, int location, int x, int y, int z", "optimumDevice.SetUniform(programId, location, x, y, z);", "GL.Uniform3(location, x, y, z);"),
        ("SetUniformArray1", "int programId, int location, int count, float[] values", "optimumDevice.SetUniformArray1(programId, location, count, values);", "GL.Uniform1(location, count, values);"),
        ("SetUniformArray2", "int programId, int location, int count, float[] values", "optimumDevice.SetUniformArray2(programId, location, count, values);", "GL.Uniform2(location, count, values);"),
        ("SetUniformArray3", "int programId, int location, int count, float[] values", "optimumDevice.SetUniformArray3(programId, location, count, values);", "GL.Uniform3(location, count, values);"),
        ("SetUniformArray4", "int programId, int location, int count, float[] values", "optimumDevice.SetUniformArray4(programId, location, count, values);", "GL.Uniform4(location, count, values);"),
        ("SetUniformMatrix", "int programId, int location, float[] matrix", "optimumDevice.SetUniformMatrix(programId, location, matrix);", "GL.UniformMatrix4(location, 1, false, matrix);"),
        ("SetUniformMatrix", "int programId, int location, ref Matrix4 matrix", "optimumDevice.SetUniformMatrix(programId, location, optimumMatrix);", "GL.UniformMatrix4(location, false, ref matrix);"),
        ("SetUniformMatrices", "int programId, int location, int count, float[] matrices", "optimumDevice.SetUniformMatrices(programId, location, count, matrices);", "GL.UniformMatrix4(location, count, false, matrices);"),
        ("SetUniformMatrices4x3", "int programId, int location, int count, float[] matrices", "optimumDevice.SetUniformMatrices4x3(programId, location, count, matrices);", "GL.UniformMatrix4x3(location, count, false, matrices);"),
        ("BindProgramTexture2D", "ShaderProgramBase program, string samplerName, int textureId, int textureNumber", "optimumDevice.BindTexture(textureNumber, textureId);", "GL.BindTexture((TextureTarget)3553, textureId);"),
        ("BindProgramTextureCube", "ShaderProgramBase program, string samplerName, int textureId, int textureNumber", "optimumDevice.BindTextureCube(textureNumber, textureId);", "GL.BindTexture((TextureTarget)34067, textureId);"),
        ("BindUBO", "UBO ubo", "optimumDevice.BindUniformBuffer(ubo.Handle);", "GL.BindBufferBase((BufferRangeTarget)35345, ubo.BindingPoint, ubo.Handle);"),
        ("UnbindUBO", "UBO ubo", "optimumDevice.UnbindUniformBuffer(ubo.Handle);", "GL.BindBuffer((BufferTarget)35345, 0);"),
        ("UpdateUBO", "UBO ubo, IntPtr data, int offset, int size, bool reallocate", "optimumDevice.UpdateUniformBuffer(ubo.Handle, data, offset, size);", "GL.BufferSubData((BufferTarget)35345, (IntPtr)offset, size, data);"),
        ("DeleteUBO", "UBO ubo", "optimumDevice.DeleteUniformBuffer(ubo.Handle);", "GL.DeleteBuffers(1, ref ubo.Handle);"),
    };

    [Theory]
    [InlineData(ProgramPath)]
    [InlineData(UboPath)]
    public void TheClassHasNoDeviceBranchAndNoDirectGlCall(string path)
    {
        string code = StripComments(ReadLib(path));

        Assert.DoesNotContain("OptimumRender.Device", code);
        Assert.DoesNotContain("IOptimumGraphicsDevice", code);
        Assert.DoesNotContain("optimumDevice", code);
        Assert.DoesNotMatch(new Regex(@"\bGL\."), code);
    }

    [Fact]
    public void ShaderProgramBaseRoutesEveryMovedOperationThroughThePlatform()
    {
        string program = StripComments(ReadLib(ProgramPath));

        var expected = new (string Method, string Call)[]
        {
            ("public void Uniform(string uniformName, float value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value);"),
            ("public void Uniform(string uniformName, int count, float[] value)", "ScreenManager.Platform.SetUniformArray1(ProgramId, uniformLocations[uniformName], count, value);"),
            ("public void Uniform(string uniformName, int value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value);"),
            ("public void Uniform(string uniformName, Vec2f value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value.X, value.Y);"),
            ("public void Uniform(string uniformName, Vec2i value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], (float)value.X, (float)value.Y);"),
            ("public void Uniform(string uniformName, float valueX, float valueY)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], valueX, valueY);"),
            ("public void Uniform(string uniformName, Vec3f value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value.X, value.Y, value.Z);"),
            ("public void Uniform(string uniformName, float valueX, float valueY, float valueZ)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], valueX, valueY, valueZ);"),
            ("public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], valueX, valueY, valueZ, valueW);"),
            ("public void Uniform(string uniformName, Vec3i value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value.X, value.Y, value.Z);"),
            ("public void Uniforms2(string uniformName, int count, float[] values)", "ScreenManager.Platform.SetUniformArray2(ProgramId, uniformLocations[uniformName], count, values);"),
            ("public void Uniforms3(string uniformName, int count, float[] values)", "ScreenManager.Platform.SetUniformArray3(ProgramId, uniformLocations[uniformName], count, values);"),
            ("public void Uniform(string uniformName, Vec4f value)", "ScreenManager.Platform.SetUniform(ProgramId, uniformLocations[uniformName], value.X, value.Y, value.Z, value.W);"),
            ("public void Uniforms4(string uniformName, int count, float[] values)", "ScreenManager.Platform.SetUniformArray4(ProgramId, uniformLocations[uniformName], count, values);"),
            ("public void UniformMatrix(string uniformName, float[] matrix)", "ScreenManager.Platform.SetUniformMatrix(ProgramId, uniformLocations[uniformName], matrix);"),
            ("public void UniformMatrix(string uniformName, ref Matrix4 matrix)", "ScreenManager.Platform.SetUniformMatrix(ProgramId, uniformLocations[uniformName], ref matrix);"),
            ("public void BindTexture2D(string samplerName, int textureId, int textureNumber)", "ScreenManager.Platform.BindProgramTexture2D(this, samplerName, textureId, textureNumber);"),
            ("public void BindTextureCube(string samplerName, int textureId, int textureNumber)", "ScreenManager.Platform.BindProgramTextureCube(this, samplerName, textureId, textureNumber);"),
            ("public void UniformMatrices4x3(string uniformName, int count, float[] matrix)", "ScreenManager.Platform.SetUniformMatrices4x3(ProgramId, uniformLocations[uniformName], count, matrix);"),
            ("public void UniformMatrices(string uniformName, int count, float[] matrix)", "ScreenManager.Platform.SetUniformMatrices(ProgramId, uniformLocations[uniformName], count, matrix);"),
            ("public void Use()", "ScreenManager.Platform.UseShaderProgram(ProgramId);"),
            ("public void Stop()", "ScreenManager.Platform.UseShaderProgram(0);"),
            ("public void Stop()", "ScreenManager.Platform.BindSampler(i, 0);"),
            ("public void Dispose()", "ScreenManager.Platform.DisposeShaderProgram(this);"),
        };

        foreach ((string method, string call) in expected)
        {
            Assert.True(Body(program, method).Contains(call, StringComparison.Ordinal), method + " does not call " + call);
        }

        // The TAA hooks stay in the program, ahead of the platform call.
        Assert.Contains("if (OptimumEntityMotion.Enabled) OptimumEntityMotion.NoteWarpUniform(uniformName, value);", program);
        Assert.Contains("if (OptimumEntityMotion.Enabled && uniformName == \"modelMatrix\") OptimumEntityMotion.NoteModelMatrix(matrix);", program);
    }

    [Fact]
    public void UboRoutesEveryMovedOperationThroughThePlatform()
    {
        string ubo = StripComments(ReadLib(UboPath));

        Assert.Contains("ScreenManager.Platform.BindUBO(this);", Body(ubo, "public override void Bind()"));
        Assert.Contains("ScreenManager.Platform.UnbindUBO(this);", Body(ubo, "public override void Unbind()"));
        Assert.Contains("ScreenManager.Platform.DeleteUBO(this);", Body(ubo, "public override void Dispose()"));
        // Update<T>(data) replaced the whole buffer on GL (glBufferData); the ranged ones write into it.
        Assert.Contains("ScreenManager.Platform.UpdateUBO(this, (IntPtr)gCHandleProvider.Pointer, 0, base.Size, true);",
            Body(ubo, "public override void Update<T>(T data)"));
        Assert.Contains("ScreenManager.Platform.UpdateUBO(this, (IntPtr)gCHandleProvider.Pointer, offset, size, false);",
            Body(ubo, "public override void Update<T>(T data, int offset, int size)"));
        Assert.Contains("ScreenManager.Platform.UpdateUBO(this, (IntPtr)num, offset, size, false);",
            Body(ubo, "public override void Update(object data, int offset, int size)"));
    }

    [Fact]
    public void TheAbstractPlatformDeclaresEveryOperationWithAnEmptyBody()
    {
        string platform = ReadLib(AbstractPath);

        foreach ((string name, string parameters, _, _) in Members)
        {
            string signature = "public virtual void " + name + "(" + parameters.Replace("ref Matrix4", "ref OpenTK.Mathematics.Matrix4") + ")";
            string inner = Regex.Replace(Body(platform, signature), @"\s+", " ").Trim();
            Assert.True(inner == "{ }", signature + " is not empty: " + inner);
        }
    }

    /// <summary>
    /// Phase 1A step 4: ClientPlatformWindows overrides every operation with the GL lines only,
    /// and VulkanClientPlatform overrides the same operation with the device call that used
    /// to be the branch in front of them.
    /// </summary>
    [Fact]
    public void ClientPlatformWindowsOverridesEveryOperationWithTheGlLinesAndVulkanClientPlatformWithTheDeviceCall()
    {
        string platform = ReadLib(WindowsPath);
        string vulkan = VulkanPlatformSource.Read();

        foreach ((string name, string parameters, string device, string gl) in Members)
        {
            string signature = "public override void " + name + "(" + parameters + ")";
            Assert.Single(Regex.Matches(platform, Regex.Escape(signature)));
            string body = Body(platform, signature);
            Assert.True(body.Contains(gl, StringComparison.Ordinal), signature + " does not issue " + gl);
            Assert.False(body.Contains("optimumDevice", StringComparison.Ordinal), signature + " still has a device branch");

            Assert.Single(Regex.Matches(vulkan, Regex.Escape(signature)));
            string deviceCall = device.Replace("optimumDevice.", "device.");
            Assert.True(Body(vulkan, signature).Contains(deviceCall, StringComparison.Ordinal),
                "VulkanClientPlatform." + name + " does not call " + deviceCall);
        }

        // The whole-buffer update keeps glBufferData on GL.
        Assert.Contains("GL.BufferData((BufferTarget)35345, size, data, (BufferUsageHint)35048);",
            Body(platform, "public override void UpdateUBO(UBO ubo, IntPtr data, int offset, int size, bool reallocate)"));
        // A unit with no custom sampler has any override cleared on the device path.
        Assert.Contains("device.BindSampler(textureNumber, 0);",
            Body(vulkan, "public override void BindProgramTexture2D(ShaderProgramBase program, string samplerName, int textureId, int textureNumber)"));
    }

    [Fact]
    public void ThePatcherInjectsTheVirtualsAndTheOverridesAndKeepsTheBodyTargets()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string abstractMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()");
        string windowsMembers = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformWindows\"] = new()");
        var names = new HashSet<string>();
        foreach ((string name, _, _, _) in Members) names.Add(name);
        foreach (string name in names)
        {
            Assert.Contains("\"" + name + "\",", abstractMembers);
            Assert.Contains("\"" + name + "\",", windowsMembers);
        }

        foreach (string target in new[]
        {
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"Use\", 0)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"Stop\", 0)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"Dispose\", 0)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"BindTexture2D\", 3)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"BindTextureCube\", 3)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"UniformMatrices\", 3)",
            "new(\"Vintagestory.Client.NoObf.ShaderProgramBase\", \"UniformMatrices4x3\", 3)",
            "new(\"Vintagestory.Client.NoObf.UBO\", \"Bind\", 0)",
            "new(\"Vintagestory.Client.NoObf.UBO\", \"Unbind\", 0)",
            "new(\"Vintagestory.Client.NoObf.UBO\", \"Dispose\", 0)",
        })
        {
            Assert.Contains(target, patcher);
        }
    }

    /// <summary>
    /// Outside the platform calls and the TAA hooks, ShaderProgramBase is vanilla again:
    /// every line the patch adds is one of those, a comment or blank.
    /// </summary>
    [Fact]
    public void ShaderProgramBaseDiffersFromVanillaOnlyByPlatformCallsAndTaaHooks()
    {
        string patch = Read("patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramBase.cs.patch");
        var offenders = new List<string>();
        foreach (string raw in patch.Split('\n'))
        {
            if (!raw.StartsWith("+", StringComparison.Ordinal) || raw.StartsWith("+++", StringComparison.Ordinal)) continue;
            string line = raw.Substring(1).Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("ScreenManager.Platform.", StringComparison.Ordinal)) continue;
            if (line.StartsWith("if (OptimumEntityMotion.Enabled", StringComparison.Ordinal)) continue;
            offenders.Add(line);
        }
        Assert.True(offenders.Count == 0, "non-routing additions:\n" + string.Join("\n", offenders));
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Block(string source, string header)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf("},", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }

    private static string ReadLib(string relativePath)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/" + relativePath));
        }
        catch (FileNotFoundException)
        {
            return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/" + relativePath + ".patch"));
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
