using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: shader compile and link, programs, uniforms, texture
// binding and uniform buffers. Each body is the device branch that opened the same
// ClientPlatformWindows method (Phase 1A step 3 moved the program/uniform/UBO ones there
// from ShaderProgramBase and UBO), moved unchanged.
//
// A uniform location here is whatever GetUniformLocation handed out - a byte offset into
// the generated block - so the setters read the same uniformLocations dictionary as GL.
// UBO.Handle carries the device's uniform-buffer handle, the convention VAO.VaoId uses for
// meshes, and the binding point survives from CreateUBO.
public partial class VulkanClientPlatform
{
    /// <summary>
    /// The device resolves the block by name against the program's reflected interface and
    /// binds it to the same point, so the four GL steps - allocate, look up the block index,
    /// bind the index, bind the buffer - collapse into one call.
    /// </summary>
    public override UBORef CreateUBO(int shaderProgramId, int bindingPoint, string blockName, int size)
    {
        UBO optimumUbo = new UBO();
        optimumUbo.Handle = device.CreateUniformBuffer(shaderProgramId, bindingPoint, blockName, size);
        optimumUbo.Size = size;
        optimumUbo.BlockName = blockName;
        optimumUbo.BindingPoint = bindingPoint;
        return optimumUbo;
    }

    public override void BindUBO(UBO ubo)
    {
        device.BindUniformBuffer(ubo.Handle);
    }

    public override void UnbindUBO(UBO ubo)
    {
        device.UnbindUniformBuffer(ubo.Handle);
    }

    /// <summary>The device writes a range whether or not the GL path would reallocate.</summary>
    public override void UpdateUBO(UBO ubo, IntPtr data, int offset, int size, bool reallocate)
    {
        device.UpdateUniformBuffer(ubo.Handle, data, offset, size);
    }

    public override void DeleteUBO(UBO ubo)
    {
        device.DeleteUniformBuffer(ubo.Handle);
    }

    public override int GetUniformLocation(ShaderProgram program, string name)
    {
        return device.GetUniformLocation(program.ProgramId, name);
    }

    public override void UseShaderProgram(int programId)
    {
        device.UseProgram(programId);
    }

    /// <summary>
    /// The device owns the SPIR-V modules inside the program and frees them with it, so
    /// there is nothing matching GL's detach-and-delete of the individual stages.
    /// </summary>
    public override void DisposeShaderProgram(ShaderProgramBase program)
    {
        foreach (KeyValuePair<string, int> optimumSampler in program.customSamplers)
        {
            device.DeleteSampler(optimumSampler.Value);
        }
        device.DeleteProgram(program.ProgramId);
    }

    public override void BindSampler(int unit, int samplerId)
    {
        device.BindSampler(unit, samplerId);
    }

    public override void SetUniform(int programId, int location, float value)
    {
        device.SetUniform(programId, location, value);
    }

    public override void SetUniform(int programId, int location, int value)
    {
        device.SetUniform(programId, location, value);
    }

    public override void SetUniform(int programId, int location, float x, float y)
    {
        device.SetUniform(programId, location, x, y);
    }

    public override void SetUniform(int programId, int location, float x, float y, float z)
    {
        device.SetUniform(programId, location, x, y, z);
    }

    public override void SetUniform(int programId, int location, float x, float y, float z, float w)
    {
        device.SetUniform(programId, location, x, y, z, w);
    }

    public override void SetUniform(int programId, int location, int x, int y, int z)
    {
        // Unlike the Vec2i overload, which casts to float before it gets here,
        // Vec3i keeps integers, so the shader declares an ivec3. The location is
        // opaque to this side, so the device lays the three components out itself.
        device.SetUniform(programId, location, x, y, z);
    }

    public override void SetUniformArray1(int programId, int location, int count, float[] values)
    {
        device.SetUniformArray1(programId, location, count, values);
    }

    public override void SetUniformArray2(int programId, int location, int count, float[] values)
    {
        device.SetUniformArray2(programId, location, count, values);
    }

    public override void SetUniformArray3(int programId, int location, int count, float[] values)
    {
        device.SetUniformArray3(programId, location, count, values);
    }

    public override void SetUniformArray4(int programId, int location, int count, float[] values)
    {
        device.SetUniformArray4(programId, location, count, values);
    }

    public override void SetUniformMatrix(int programId, int location, float[] matrix)
    {
        device.SetUniformMatrix(programId, location, matrix);
    }

    public override void SetUniformMatrix(int programId, int location, ref Matrix4 matrix)
    {
        // Only the sun and moon renderers use this overload, a handful of
        // times per frame, so flattening into an array here is not worth a
        // dedicated entry point on the seam.
        float[] optimumMatrix = new float[16];
        optimumMatrix[0] = matrix.M11; optimumMatrix[1] = matrix.M12;
        optimumMatrix[2] = matrix.M13; optimumMatrix[3] = matrix.M14;
        optimumMatrix[4] = matrix.M21; optimumMatrix[5] = matrix.M22;
        optimumMatrix[6] = matrix.M23; optimumMatrix[7] = matrix.M24;
        optimumMatrix[8] = matrix.M31; optimumMatrix[9] = matrix.M32;
        optimumMatrix[10] = matrix.M33; optimumMatrix[11] = matrix.M34;
        optimumMatrix[12] = matrix.M41; optimumMatrix[13] = matrix.M42;
        optimumMatrix[14] = matrix.M43; optimumMatrix[15] = matrix.M44;
        device.SetUniformMatrix(programId, location, optimumMatrix);
    }

    public override void SetUniformMatrices(int programId, int location, int count, float[] matrices)
    {
        device.SetUniformMatrices(programId, location, count, matrices);
    }

    public override void SetUniformMatrices4x3(int programId, int location, int count, float[] matrices)
    {
        device.SetUniformMatrices4x3(programId, location, count, matrices);
    }

    /// <summary>
    /// In GL this is three separate things - point the sampler uniform at a unit, activate
    /// that unit, bind the texture. The device keeps the same split so the two halves can
    /// be set independently, which the render systems rely on.
    /// </summary>
    public override void BindProgramTexture2D(ShaderProgramBase program, string samplerName, int textureId, int textureNumber)
    {
        device.SetSamplerUnit(program.ProgramId, samplerName, textureNumber);
        device.BindTexture(textureNumber, textureId);
        if (program.customSamplers.TryGetValue(samplerName, out var optimumSampler))
        {
            device.BindSampler(textureNumber, optimumSampler);
        }
        else
        {
            // Clear any override left on this unit, or the texture's own
            // filtering would be silently ignored.
            device.BindSampler(textureNumber, 0);
        }
        if (program.clampTToEdge)
        {
            device.SetTextureParameter(textureId,
                Vintagestory.API.Config.OptimumGlConstants.TextureWrapT,
                Vintagestory.API.Config.OptimumGlConstants.ClampToEdge);
        }
    }

    public override void BindProgramTextureCube(ShaderProgramBase program, string samplerName, int textureId, int textureNumber)
    {
        device.SetSamplerUnit(program.ProgramId, samplerName, textureNumber);
        device.BindTextureCube(textureNumber, textureId);
        if (program.clampTToEdge)
        {
            device.SetTextureParameter(textureId,
                Vintagestory.API.Config.OptimumGlConstants.TextureWrapT,
                Vintagestory.API.Config.OptimumGlConstants.ClampToEdge);
        }
    }

    /// <summary>
    /// The device only stages the stage here. GL resolves uniforms and varyings by name
    /// across the whole program, so nothing about a stage is final until its siblings are
    /// known, and the real translation happens at link time.
    /// </summary>
    public override bool CompileShader(Shader shader)
    {
        return device.CompileShader(shader);
    }

    /// <summary>
    /// The device assigns the id, exactly as glCreateProgram did, and the caller stores it -
    /// IShaderProgram.ProgramId is read-only on the interface, so it comes back as a
    /// return value.
    /// </summary>
    public override bool CreateShaderProgram(ShaderProgram program)
    {
        int optimumProgramId = device.LinkProgram(program);
        if (optimumProgramId == 0)
        {
            string optimumLinkError = device.GetError();
            Logger.Error("Link error in shader program for pass {0}: {1}",
                program.PassName, optimumLinkError == null ? "unknown" : optimumLinkError);
            return false;
        }
        program.ProgramId = optimumProgramId;
        Logger.Notification("Loaded Shaderprogramm for render pass {0}.", program.PassName);
        return true;
    }
}
