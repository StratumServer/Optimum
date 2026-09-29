using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    // -------------------------------------------------------------------- shaders

    /// <summary>
    /// Stages a shader. No SPIR-V is produced here because GL resolves uniforms
    /// and varyings by name across the whole program, so nothing about a stage is
    /// final until its siblings are known.
    /// </summary>
    /// <summary>
    /// Largest shader source accepted per stage. Vanilla's biggest stage is
    /// well under 100 KiB; the cap keeps a broken or hostile mod shader from
    /// handing the native compiler an unbounded input.
    /// </summary>
    internal const int MaxShaderSourceBytes = 2 * 1024 * 1024;

    public bool CompileShader(IShader shader)
    {
        if (shader?.Code == null) return false;

        string stageName = shader.Type.ToString();
        if (shader.Code.Length + (shader.PrefixCode?.Length ?? 0) > MaxShaderSourceBytes)
        {
            AddDiagnostic($"{stageName}: shader source exceeds {MaxShaderSourceBytes} bytes and was rejected");
            return false;
        }
        if (shader.Code.IndexOf('\0') >= 0 || (shader.PrefixCode?.IndexOf('\0') ?? -1) >= 0)
        {
            AddDiagnostic($"{stageName}: shader source contains a NUL byte and was rejected");
            return false;
        }

        _stagedStages[shader] = new StagedStage
        {
            Stage = shader.Type,
            Code = shader.Code,
            PrefixCode = shader.PrefixCode ?? "",
            Filename = shader.Type.ToString(),
        };
        return true;
    }

    public int LinkProgram(IShaderProgram program)
    {
        var stages = new List<ShaderStageSource>();
        AddStage(stages, program.VertexShader, EnumShaderType.VertexShader, program.PassName);
        AddStage(stages, program.FragmentShader, EnumShaderType.FragmentShader, program.PassName);
        AddStage(stages, program.GeometryShader, EnumShaderType.GeometryShader, program.PassName);

        if (stages.Count == 0)
        {
            AddDiagnostic($"shader program '{program.PassName}' has no stages");
            return 0;
        }

        // The seam (docs/vulkan.md): a program the manifest has links from its
        // SPIR-V; one it has not links through the rewriter; one it has but cannot serve (a bad hash, an
        // unreadable define, a module the driver refuses) links through the rewriter and counts as failed.
        string passName = program.PassName ?? "";
        ShaderProgramResources? resources = null;
        TranslatedProgram? native = null;
        bool nativeFailed = false;
        string nativeDetail = "";
        if (_nativeShaders != null && _modShaderScan != null && _modShaderScan(passName))
        {
            // A mod replaced this program's GLSL (or the scan cannot rule it out): the native SPIR-V would draw
            // vanilla over the mod, so the mod's source goes through the rewriter. Rewritten, not failed.
            ReportModOverride(passName);
        }
        else if (_nativeShaders != null)
        {
            NativeShaderLibrary.Outcome outcome = _nativeShaders.TryLink(passName, stages, out native, out nativeDetail);
            nativeFailed = outcome == NativeShaderLibrary.Outcome.Failed;
            if (outcome != NativeShaderLibrary.Outcome.Native) native = null;
        }

        int programId = 0;
        if (native != null)
        {
            programId = _nextProgramId++;
            try
            {
                resources = new ShaderProgramResources(_context, programId, native, _sharedLayout!.Layout);
            }
            catch (InvalidOperationException error)
            {
                nativeFailed = true;
                nativeDetail += ": " + error.Message;
            }
        }
        if (nativeFailed) ReportNativeFailure(passName, nativeDetail);

        TranslatedProgram translated;
        if (resources != null)
        {
            translated = native!;
            _nativeLinks++;
        }
        else
        {
            if (nativeFailed) _failedNativeLinks++;
            else _rewrittenLinks++;

            // The include files the registry assembled the program from decide which
            // of its uniforms read the shared frame block. A program built any other
            // way - a mod's, a test's - keeps every uniform to itself.
            translated = ShaderTranslator.Translate(stages, _shaderCompiler, null,
                (program as Vintagestory.Client.NoObf.ShaderProgramBase)?.includes);
            if (!translated.Success)
            {
                foreach (string error in translated.Errors)
                {
                    AddDiagnostic($"{program.PassName}: {error}");
                }
                return 0;
            }

            if (programId == 0) programId = _nextProgramId++;
        }
        if (RenderTrace.Enabled)
        {
            RenderTrace.DumpProgramSources(program.PassName, translated);
            RenderTrace.Write("program " + programId + " '" + program.PassName + "' uniformBlockBytes=" +
                translated.Layout.BlockSize + " pushBytes=" + translated.Layout.PushConstantSize +
                (translated.IsNative ? " native [" + nativeDetail + "]" : ""));
            foreach (UniformMember member in translated.Layout.Members)
            {
                RenderTrace.Write("  uniform " + member.Name + " offset=" + member.Offset +
                    " type=" + member.Type + " count=" + member.ArrayLength);
            }
        }
        resources ??= new ShaderProgramResources(_context, programId, translated, _sharedLayout!.Layout);
        _programs[programId] = resources;
        // The variant a native program was linked for (TryLink reports the key as its detail),
        // so a native pipeline request can state the variant it expects.
        if (resources.IsNative) _programVariants[programId] = nativeDetail;
        _programNames[programId] = program.PassName ?? "";
        // Pipelines an earlier launch used with this exact program start compiling now.
        int prewarming = _pipelines.PrewarmFor(resources);
        if (prewarming > 0 && RenderTrace.Enabled)
        {
            RenderTrace.Write("program " + programId + " prewarming " + prewarming + " pipelines");
        }
        return programId;
    }

    /// <summary>
    /// Loads the native shaders once, at device start: the manifest beside the renderer assembly, the directory a
    /// test named, or the source tree <c>OPTIMUM_VK_SHADER_SOURCE</c> names. One log line says what came of it.
    /// </summary>
    private void LoadNativeShaders()
    {
        string? assemblyDirectory = null;
        try
        {
            assemblyDirectory = System.IO.Path.GetDirectoryName(typeof(VulkanDevice).Assembly.Location);
        }
        catch (Exception error) when (error is ArgumentException or System.IO.PathTooLongException)
        {
            // An assembly loaded from bytes has no location; the resolution below reports it.
        }

        (NativeShaderLibrary.Mode mode, string? path, string reason) = NativeShaderLibrary.Resolve(
            NativeShadersEnabled, NativeShaderDirectory,
            Environment.GetEnvironmentVariable(NativeShaderLibrary.EnabledVariable),
            Environment.GetEnvironmentVariable(NativeShaderLibrary.SourceVariable),
            assemblyDirectory);

        _nativeShaders = mode switch
        {
            NativeShaderLibrary.Mode.Directory => NativeShaderLibrary.Load(path!, _shaderCompiler.Identity, out reason),
            NativeShaderLibrary.Mode.Source => NativeShaderLibrary.BuildFromSource(path!, _shaderCompiler, out reason),
            _ => null,
        };

        string enabledVariable = Environment.GetEnvironmentVariable(NativeShaderLibrary.EnabledVariable) ?? "";
        bool ignoreScan = IgnoreModShaderScan ?? NativeShaderLibrary.IgnoresModScan(enabledVariable);
        _modShaderScan = ignoreScan ? null : ShaderProgramOverriddenByMods;

        NativeShaderStatus = _nativeShaders == null
            ? "off: " + reason
            : _nativeShaders.Manifest.Programs.Count + " programs from " + _nativeShaders.Origin +
              (reason.Length > 0 ? "; " + reason : "");
        if (_nativeShaders != null && ignoreScan && ShaderProgramOverriddenByMods != null)
        {
            NativeShaderStatus += "; mod shader scan ignored (" + NativeShaderLibrary.EnabledVariable + "=" + NativeShaderLibrary.ForceValue + ")";
        }
        else if (_nativeShaders != null && _modShaderScan != null && _modShaderScan(AllShaderProgramsEntry))
        {
            NativeShaderStatus += "; the mod shader scan makes every program rewriter-only (no report, a failed scan, or a shaderincludes override)";
        }
        LogShaderLine("[Optimum] shaders: native " + NativeShaderStatus);
    }

    /// <summary>The scan's entry for every program (<c>OptimumConfig.AllShaderPrograms</c>, the scanner's <c>AllPrograms</c>).</summary>
    internal const string AllShaderProgramsEntry = "all";

    /// <summary>Logs, once per program the manifest has, that the mod shader scan sent it to the rewriter.</summary>
    private void ReportModOverride(string passName)
    {
        if (_nativeShaders?.Manifest.FindProgram(passName) == null) return;
        bool all = _modShaderScan!(AllShaderProgramsEntry);
        lock (_modOverrideLogged)
        {
            if (!_modOverrideLogged.Add(passName)) return;
        }
        string line = "[Optimum] shaders: native '" + passName + "' linked through the rewriter: " +
            (all ? "the mod shader scan makes every program rewriter-only" : "a mod replaces its GLSL (launcher shader scan)");
        LogShaderLine(line);
        if (RenderTrace.Enabled) RenderTrace.Write(line);
    }

    private void ReportNativeFailure(string passName, string detail)
    {
        string line = "[Optimum] shaders: native '" + passName + "' failed, linked through the rewriter: " + detail;
        LogShaderLine(line);
        if (RenderTrace.Enabled) RenderTrace.Write(line);
    }

    /// <summary>
    /// The line after a shader load: the programs linked since the last report. ShaderRegistry links every program
    /// of a load or reload in one synchronous call, so the first frame after links is the end of that load.
    /// </summary>
    private void ReportShaderLoad()
    {
        int native = _nativeLinks - _nativeLinksReported;
        int rewritten = _rewrittenLinks - _rewrittenLinksReported;
        int failed = _failedNativeLinks - _failedNativeLinksReported;
        if (native + rewritten + failed == 0) return;

        _nativeLinksReported = _nativeLinks;
        _rewrittenLinksReported = _rewrittenLinks;
        _failedNativeLinksReported = _failedNativeLinks;
        LogShaderLine("[Optimum] shaders: " + native + " native, " + rewritten + " rewritten, " + failed + " failed");
        VulkanStats.NoteShaderLoad(native, rewritten, failed);
    }

    private static void LogShaderLine(string line)
    {
        Console.WriteLine(line);
        MirrorValidationMessage("--- " + line);
    }

    private void AddStage(List<ShaderStageSource> stages, IShader? shader, EnumShaderType stage, string passName)
    {
        if (shader == null || !_stagedStages.TryGetValue(shader, out StagedStage? staged)) return;

        stages.Add(new ShaderStageSource
        {
            Stage = stage,
            Code = staged.Code,
            PrefixCode = staged.PrefixCode,
            Filename = passName + StageExtension(stage),
        });
    }

    private static string StageExtension(EnumShaderType stage) => stage switch
    {
        EnumShaderType.VertexShader => ".vsh",
        EnumShaderType.FragmentShader => ".fsh",
        _ => ".gsh",
    };

    public void DeleteProgram(int programId)
    {
        if (!_programs.Remove(programId, out ShaderProgramResources? program)) return;
        _programNames.Remove(programId);
        // No background compile may still be reading its modules or layout.
        _pipelines.CancelProgram(program);
        ForgetNativePipelines(programId);
        _frames.DeferDeletion(program);
    }

    public int GetUniformLocation(int programId, string name) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) ? program.LocationOf(name) : -1;

    // ------------------------------------------------------------------- uniforms

    private void Write(int programId, int location, ReadOnlySpan<byte> data)
    {
        // A member of the shared frame block: one shadow for every program.
        if (ShaderProgramResources.IsFrameLocation(location))
        {
            WriteFrameGlobal(location - ShaderProgramResources.FrameLocationBase, data);
            return;
        }

        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            // A native program's push member (a DRAW uniform): kept per program, pushed per draw.
            if (ShaderProgramResources.IsPushLocation(location)) program.SetPushUniform(location, data);
            else program.SetUniform(location, data);
        }
    }

    /// <summary>
    /// Writes into the shared frame block. Use() rewrites the same values on every
    /// program switch, so the common case is bytes that already match: a comparison
    /// and nothing else. A real change bumps the version and the next draw takes a
    /// new snapshot.
    /// </summary>
    private void WriteFrameGlobal(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > _frameGlobals.Length) return;

        Span<byte> destination = _frameGlobals.AsSpan(offset, data.Length);
        if (data.SequenceEqual(destination)) return;

        data.CopyTo(destination);
        _frameGlobalsVersion++;
    }

    /// <summary>A copy of a linked program's record shadow, initializers included; null for an unknown program. For tests.</summary>
    internal byte[]? ProgramRecordForTests(int programId) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) ? (byte[])program.UniformShadow.Clone() : null;

    /// <summary>A copy of the shared frame block's current bytes. For tests.</summary>

    public void SetUniform(int programId, int location, float value) =>
        Write(programId, location, new ReadOnlySpan<byte>(&value, sizeof(float)));

    public void SetUniform(int programId, int location, int value)
    {
        // Assigning a sampler its texture unit is an int write to its uniform
        // location in GL. Here the sampler is a descriptor binding, so the same
        // call has to reach the unit table instead of the uniform block.
        if (ShaderProgramResources.IsSamplerLocation(location))
        {
            if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
            {
                program.SetSamplerUnitByLocation(location, value);
            }
            return;
        }
        Write(programId, location, new ReadOnlySpan<byte>(&value, sizeof(int)));
    }

    public void SetUniform(int programId, int location, int x, int y, int z)
    {
        // Scalar block layout stores an ivec3 as three consecutive 32-bit ints.
        int* values = stackalloc int[3] { x, y, z };
        Write(programId, location, new ReadOnlySpan<byte>(values, 3 * sizeof(int)));
    }

    public void SetUniform(int programId, int location, float x, float y)
    {
        float* values = stackalloc float[2] { x, y };
        Write(programId, location, new ReadOnlySpan<byte>(values, 2 * sizeof(float)));
    }

    public void SetUniform(int programId, int location, float x, float y, float z)
    {
        float* values = stackalloc float[3] { x, y, z };
        Write(programId, location, new ReadOnlySpan<byte>(values, 3 * sizeof(float)));
    }

    public void SetUniform(int programId, int location, float x, float y, float z, float w)
    {
        float* values = stackalloc float[4] { x, y, z, w };
        Write(programId, location, new ReadOnlySpan<byte>(values, 4 * sizeof(float)));
    }

    // The array setters are a straight memcpy because the generated block uses
    // scalar layout, where a float[] packs exactly as the shader expects. Under
    // std140 each of these would need re-striding on the way in.
    private void WriteArray(int programId, int location, int count, float[] values, int componentsPerElement)
    {
        int floats = Math.Min(values.Length, count * componentsPerElement);
        if (floats <= 0) return;

        fixed (float* source = values)
        {
            Write(programId, location, new ReadOnlySpan<byte>(source, floats * sizeof(float)));
        }
    }

    public void SetUniformArray1(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 1);

    public void SetUniformArray2(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 2);

    public void SetUniformArray3(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 3);

    public void SetUniformArray4(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 4);

    public void SetUniformMatrix(int programId, int location, float[] matrix) =>
        WriteArray(programId, location, 1, matrix, 16);

    public void SetUniformMatrices(int programId, int location, int count, float[] matrices) =>
        WriteArray(programId, location, count, matrices, 16);

    public void SetUniformMatrices4x3(int programId, int location, int count, float[] matrices) =>
        WriteArray(programId, location, count, matrices, 12);

    /// <summary>
    /// The samplers a linked program declares, in declaration order.
    ///
    /// Not part of the seam - the client never needs it, because it binds the
    /// samplers it knows by name. It exists so a test can bind every sampler a
    /// real program declares without hardcoding the list, since a draw whose
    /// descriptor set is incomplete is skipped rather than drawn.
    /// </summary>
    internal string[] SamplerNamesOf(int programId)
    {
        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            return program.SamplerNames;
        }
        return Array.Empty<string>();
    }

    public void SetSamplerUnit(int programId, string samplerName, int unit)
    {
        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            program.SetSamplerUnitByName(samplerName, unit);
        }
    }

    // ------------------------------------------------------------ uniform buffers

    /// <summary>
    /// One uniform buffer object the client created for a named block.
    ///
    /// The CPU shadow is the source of truth, not the GPU buffer. The client
    /// updates one UBO per block and re-updates it between draws - the entity
    /// renderer uploads the "Animation" block once per entity, immediately before
    /// that entity's draw - but a draw is only recorded here, not executed, so a
    /// buffer written in place would give every entity in the frame the last
    /// entity's transforms. Writes therefore land in ordinary memory and a draw
    /// snapshots them into the frame's uniform ring, exactly as the generated
    /// block does.
    ///
    /// There is deliberately no GPU buffer per block: the snapshot goes in the
    /// ring, and the ring-exhausted path allocates its own transient copy for
    /// that one draw, so a persistent buffer would only ever sit unbound.
    /// </summary>
    private sealed class ClientUniformBuffer
    {
        public ClientUniformBuffer(byte[] shadow, string blockName)
        {
            Shadow = shadow;
            BlockName = blockName;
        }

        public byte[] Shadow { get; }
        public string BlockName { get; }

        /// <summary>Bumped by every write, so an unchanged block reuses its snapshot.</summary>
        public uint Version { get; private set; } = 1;

        /// <summary>Which frame's ring the snapshot below lives in, and what it holds.</summary>
        public uint SnapshotFrame { get; private set; }
        public uint SnapshotVersion { get; private set; }
        public uint SnapshotOffset { get; private set; }

        public void Write(IntPtr data, int offset, int size)
        {
            // A client that re-uploads identical bytes before every draw would
            // otherwise cost a fresh ring slice per draw; comparing is cheaper.
            var incoming = new ReadOnlySpan<byte>((void*)data, size);
            Span<byte> target = Shadow.AsSpan(offset, size);
            if (incoming.SequenceEqual(target)) return;
            incoming.CopyTo(target);
            Version++;
        }

        public void NoteSnapshot(uint frame, uint offset)
        {
            SnapshotFrame = frame;
            SnapshotVersion = Version;
            SnapshotOffset = offset;
        }

        public bool HasSnapshotFor(uint frame) => SnapshotFrame == frame && SnapshotVersion == Version;
    }

    private readonly Dictionary<int, ClientUniformBuffer> _uniformBuffers = new();

    /// <summary>
    /// The buffer currently supplying each named block.
    ///
    /// The client's UBO binds with glBindBufferBase to binding point 0 and names
    /// the block when it creates the buffer, so the block name is what actually
    /// identifies which declaration a buffer feeds. Vulkan has no such global
    /// binding point, so the association is kept here and resolved per draw
    /// against the program's own declared blocks.
    /// </summary>
    private readonly Dictionary<string, int> _boundUniformBuffers = new(StringComparer.Ordinal);

    private int _nextUniformBufferId = 1;

    public int CreateUniformBuffer(int programId, int bindingPoint, string blockName, int size)
    {
        int bytes = Math.Max(size, 4);
        int id = _nextUniformBufferId++;
        _uniformBuffers[id] = new ClientUniformBuffer(new byte[bytes], blockName ?? "");

        // GL's glBindBufferBase in the client's constructor takes effect at once,
        // and a buffer is only ever created to be used.
        if (!string.IsNullOrEmpty(blockName)) _boundUniformBuffers[blockName] = id;
        return id;
    }

    public void UpdateUniformBuffer(int handle, IntPtr data, int offset, int size)
    {
        if (!_uniformBuffers.TryGetValue(handle, out ClientUniformBuffer? ubo)) return;
        if (data == IntPtr.Zero || offset < 0 || size < 0) return;
        if ((long)offset + size > ubo.Shadow.Length) return;

        ubo.Write(data, offset, size);
    }

    public void BindUniformBuffer(int handle)
    {
        if (_uniformBuffers.TryGetValue(handle, out ClientUniformBuffer? ubo) && ubo.BlockName.Length > 0)
        {
            _boundUniformBuffers[ubo.BlockName] = handle;
        }
    }

    /// <summary>
    /// Deliberately does not break the block association.
    ///
    /// The client's Unbind is glBindBuffer(UNIFORM_BUFFER, 0), which clears the
    /// generic target and leaves the glBindBufferBase index binding standing -
    /// and the index binding is what feeds the shader. Dropping the association
    /// here would unbind the block the client still expects to be supplied.
    /// </summary>
    public void UnbindUniformBuffer(int handle) { }

    public void DeleteUniformBuffer(int handle)
    {
        if (!_uniformBuffers.Remove(handle, out ClientUniformBuffer? ubo)) return;

        if (ubo.BlockName.Length > 0 &&
            _boundUniformBuffers.TryGetValue(ubo.BlockName, out int bound) && bound == handle)
        {
            _boundUniformBuffers.Remove(ubo.BlockName);
        }
    }
}
