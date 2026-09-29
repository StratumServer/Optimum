using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>Shared shader and uniform setup for the independent motion scenarios.</summary>
internal static class MotionFixture
{
    internal readonly record struct MotionTarget(int Framebuffer, int ColorTexture, int MotionTexture);

    internal static MotionTarget CreateMotionTarget(VulkanDevice device, int size)
    {
        int color = device.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int glow = device.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int motion = device.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba16f,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int depth = device.CreateTexture2D(size, size, EnumTextureInternalFormat.DepthComponent32,
            EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);

        int framebuffer = device.CreateFramebuffer(size, size);
        device.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, color, 0);
        device.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment1, glow, 0);
        device.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment2, motion, 0);
        device.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
        device.SetDrawBuffers(framebuffer, 0b111);
        Assert.True(device.CheckFramebufferComplete(framebuffer, out string status), status);
        return new MotionTarget(framebuffer, color, motion);
    }

    internal static float[] ReadMotion(VulkanDevice device, int texture, int size)
    {
        OptimumTextureReadback? readback = device.ReadTextureForParity(texture);
        Assert.NotNull(readback);
        Assert.Equal(size, readback.Width);
        Assert.Equal(size, readback.Height);
        Assert.NotNull(readback.Floats);
        Assert.Equal(size * size * 4, readback.Floats.Length);
        return readback.Floats;
    }

    internal static MeshData CreateFaceData(float z = 0, int flags = 7 << 18)
    {
        var face = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);
        float[] xy = [-0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f];
        float[] uv = [0, 0, 1, 0, 1, 1, 0, 1];
        for (int i = 0; i < 4; i++)
            face.AddVertexWithFlags(xy[i * 2], xy[i * 2 + 1], z,
                uv[i * 2], uv[i * 2 + 1], Vintagestory.API.MathTools.ColorUtil.WhiteArgb,
                flags);
        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 }) face.AddIndex(index);
        return face;
    }

    internal static int CreateFaceMesh(VulkanDevice device, float z = 0)
    {
        int mesh = device.CreateMesh(CreateFaceData(z), staticDraw: true);
        Assert.True(mesh > 0, device.GetError() ?? "motion face upload failed");
        return mesh;
    }

    internal static void SetFloat(VulkanDevice seam, int program, string name, float value)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, value);
    }

    internal static void SetInt(VulkanDevice seam, int program, string name, int value)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, value);
    }

    internal static void SetFloat2(VulkanDevice seam, int program, string name, float x, float y)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, x, y);
    }

    internal static void SetFloat3(VulkanDevice seam, int program, string name, float x, float y, float z)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, x, y, z);
    }

    internal static void SetFloat4(
        VulkanDevice seam, int program, string name, float x, float y, float z, float w)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniform(program, location, x, y, z, w);
    }

    internal static void SetMatrix(VulkanDevice seam, int program, string name, float[] matrix)
    {
        int location = seam.GetUniformLocation(program, name);
        if (location >= 0) seam.SetUniformMatrix(program, location, matrix);
    }

    internal static unsafe int CreateWhiteTexture(VulkanDevice seam)
    {
        var white = new byte[] { 255, 255, 255, 255 };
        fixed (byte* pixels = white)
        {
            return seam.CreateTexture2D(1, 1,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
        }
    }

    internal static int BindEveryDeclaredSampler(
        VulkanDevice device, VulkanDevice seam, int programId)
    {
        int unit = 0;
        foreach (string samplerName in device.SamplerNamesOf(programId))
        {
            int texture = CreateWhiteTexture(seam);
            seam.SetSamplerUnit(programId, samplerName, unit);
            seam.BindTexture(unit, texture);
            unit++;
        }
        return unit;
    }

    internal static int LinkFromCorpus(
        VulkanDevice seam, List<ShaderStageSource> stages, string name, bool oit)
    {
        var program = new CorpusProgram { PassName = name, Oit = oit };

        foreach (ShaderStageSource stage in stages)
        {
            var shader = new CorpusShader
            {
                Type = stage.Stage,
                Code = stage.Code,
                PrefixCode = stage.PrefixCode ?? "",
            };
            Assert.True(seam.CompileShader(shader), name + ": " + (seam.GetError() ?? "compile failed"));

            if (stage.Stage == EnumShaderType.VertexShader) program.VertexShader = shader;
            else if (stage.Stage == EnumShaderType.FragmentShader) program.FragmentShader = shader;
            else program.GeometryShader = shader;
        }

        int programId = seam.LinkProgram(program);
        Assert.True(programId > 0, name + ": " + (seam.GetError() ?? "link failed"));
        return programId;
    }

    private sealed class CorpusShader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    private sealed class CorpusProgram : IShaderProgram
    {
        public int ProgramId { get; set; }
        public string AssetDomain { get; set; } = "game";
        public int PassId { get; set; }
        public string PassName { get; set; } = "";
        public bool ClampTexturesToEdge { get; set; }
        public IShader VertexShader { get; set; } = null!;
        public IShader FragmentShader { get; set; } = null!;
        public IShader GeometryShader { get; set; } = null!;
        public bool Oit { get; set; }
        public bool Disposed => false;
        public bool LoadError => false;
        public Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> UBOs { get; } = new();
        public bool Compile() => true;
        public bool HasUniform(string uniformName) => false;
        public void Use() { }
        public void Stop() { }
        public void Dispose() { }
        public void Uniform(string uniformName, float value) { }
        public void Uniform(string uniformName, int value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2f value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2i value) { }
        public void Uniform(string uniformName, float valueX, float valueY) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec3f value) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec4f value) { }
        public void Uniforms4(string uniformName, int count, float[] values) { }
        public void UniformMatrix(string uniformName, float[] matrix) { }
        public void BindTexture2D(string samplerName, int textureId, int textureNumber) { }
        public void BindTextureCube(string samplerName, int textureId, int textureNumber) { }
        public void UniformMatrices(string uniformName, int count, float[] matrix) { }
        public void UniformMatrices4x3(string uniformName, int count, float[] matrix) { }
    }
}
