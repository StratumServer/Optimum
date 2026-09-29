using System;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Optimum.Render.Vulkan.AmbientOcclusion;

/// <summary>
/// The AO pipeline on the device (docs/vulkan.md#ambient-occlusion section C): prefilter
/// (Primary depth to five R32F levels), main (visibility term and packed edges) and the
/// edge-aware denoise, as frame-graph compute passes. Owns its targets, sized to the depth
/// it is given, and its programs, compiled for the storage formats the device chose.
///
/// The output is the visibility at render resolution, unscaled from the 1.5 packing, with
/// sky and hand-view pixels exactly 1; the compose pass (scene-ssao, OPTIMUMAO) multiplies
/// it into Primary colour 0 before the TAA resolve and applies the water, fog and OIT
/// attenuation there, so this term stays a pure visibility for measurement.
/// </summary>
internal sealed class GtaoRenderer : IDisposable
{
    public const int DepthLevels = 5;

    /// <summary>The smallest depth the five-level chain covers.</summary>
    public const int MinimumExtent = 16;

    private readonly VulkanDevice _device;
    private int _hilbert;
    private int _workingDepth;
    private int _workingTerm;
    private int _edges;
    private int _output;
    private int _scratchA;
    private int _scratchB;
    private uint _width;
    private uint _height;
    private int _prefilter;
    private int _main;
    private int _denoise;
    private Format _programDepthFormat;
    private Format _programTermFormat;

    public GtaoRenderer(VulkanDevice device) => _device = device;

    /// <summary>Why the last <see cref="Render" /> returned 0.</summary>
    public string? LastError { get; private set; }

    /// <summary>True once the programs failed to compile: a device condition, not a frame's.</summary>
    public bool ProgramsFailed { get; private set; }

    /// <summary>Working depth, five levels of view-space depth (the debug output "mip 0" is level 0).</summary>
    public int WorkingDepthTexture => _workingDepth;

    /// <summary>The pre-denoise term, visibility / 1.5.</summary>
    public int WorkingTermTexture => _workingTerm;

    /// <summary>The packed 2-bit edges.</summary>
    public int EdgesTexture => _edges;

    /// <summary>The denoised visibility.</summary>
    public int OutputTexture => _output;

    public int HilbertTexture => _hilbert;

    /// <summary>
    /// Records the three passes into the open frame and returns <see cref="OutputTexture" />,
    /// or 0 with <see cref="LastError" /> set when nothing could be recorded.
    /// </summary>
    /// <param name="depthTexture">Primary's D32 depth.</param>
    /// <param name="normalTexture">Primary colour 2 (gNormal): GL view-space normal, class in w.</param>
    /// <param name="projection">The column-major GL projection the G-buffer was drawn with.</param>
    /// <param name="settings">Preset, variants and uniforms.</param>
    /// <param name="noiseIndex">The frame index while TAA accumulates, 0 otherwise.</param>
    public int Render(int depthTexture, int normalTexture, float[] projection, GtaoSettings settings, uint noiseIndex)
    {
        LastError = null;
        GtaoProjection? reconstruction = GtaoProjection.From(projection);
        if (reconstruction == null) return Fail("the projection is not a GL perspective matrix");

        VulkanTexture? depth = _device.TextureOf(depthTexture);
        VulkanTexture? normal = _device.TextureOf(normalTexture);
        if (depth == null || normal == null) return Fail("depth or normal texture missing");
        if (depth.Width != normal.Width || depth.Height != normal.Height) return Fail("depth and normal sizes differ");
        if (depth.Width < MinimumExtent || depth.Height < MinimumExtent) return Fail("the target is smaller than 16x16");

        if (!EnsureTargets(depth.Width, depth.Height)) return Fail("storage targets could not be created");
        if (!EnsurePrograms()) return 0;

        byte[] push = settings.PushConstants(reconstruction.Value, noiseIndex);

        uint blocksX = (depth.Width + 15) / 16;
        uint blocksY = (depth.Height + 15) / 16;
        var prefilterBindings = new ComputeBinding[1 + DepthLevels];
        prefilterBindings[0] = new ComputeBinding(0, depthTexture, ComputeAccess.Sampled);
        for (uint level = 0; level < DepthLevels; level++)
        {
            prefilterBindings[1 + level] = new ComputeBinding(1 + level, _workingDepth, ComputeAccess.StorageWrite, BaseMip: level);
        }
        if (!_device.RecordComputePass(new ComputePassDeclaration
        {
            Name = "gtao-prefilter",
            ProgramId = _prefilter,
            Bindings = prefilterBindings,
            Dispatches = new[] { ComputeDispatch.Explicit((blocksX + 7) / 8, (blocksY + 7) / 8, 1, push) },
        })) return Fail(_device.GetError());

        if (!_device.RecordComputePass(new ComputePassDeclaration
        {
            Name = "gtao-main",
            ProgramId = _main,
            Specialization = settings.MainSpecialization(),
            Bindings = new[]
            {
                new ComputeBinding(0, _workingDepth, ComputeAccess.Sampled, 0, DepthLevels),
                new ComputeBinding(1, depthTexture, ComputeAccess.Sampled),
                new ComputeBinding(2, normalTexture, ComputeAccess.Sampled),
                new ComputeBinding(3, _hilbert, ComputeAccess.Sampled),
                new ComputeBinding(4, _workingTerm, ComputeAccess.StorageWrite),
                new ComputeBinding(5, _edges, ComputeAccess.StorageWrite),
            },
            Dispatches = new[] { ComputeDispatch.Covering(4, push) },
        })) return Fail(_device.GetError());

        uint passes = Math.Clamp(settings.DenoisePasses, 1, 3);
        int source = _workingTerm;
        for (uint pass = 0; pass < passes; pass++)
        {
            bool final = pass + 1 == passes;
            int destination = final ? _output : pass % 2 == 0 ? _scratchA : _scratchB;
            if (!_device.RecordComputePass(new ComputePassDeclaration
            {
                Name = final ? "gtao-denoise" : "gtao-denoise-pre",
                ProgramId = _denoise,
                Specialization = GtaoSettings.DenoiseSpecialization(final),
                Bindings = new[]
                {
                    new ComputeBinding(0, source, ComputeAccess.Sampled),
                    new ComputeBinding(1, _edges, ComputeAccess.Sampled),
                    new ComputeBinding(2, depthTexture, ComputeAccess.Sampled),
                    new ComputeBinding(3, normalTexture, ComputeAccess.Sampled),
                    new ComputeBinding(4, destination, ComputeAccess.StorageWrite),
                },
                Dispatches = new[] { ComputeDispatch.Covering(4, push) },
            })) return Fail(_device.GetError());
            source = destination;
        }
        return _output;
    }

    private int Fail(string reason)
    {
        LastError = string.IsNullOrEmpty(reason) ? "compute pass refused" : reason;
        return 0;
    }

    private unsafe bool EnsureTargets(uint width, uint height)
    {
        if (_hilbert == 0)
        {
            float[] table = HilbertLut.Build();
            fixed (float* data = table)
            {
                _hilbert = _device.CreateTexture2DRaw(HilbertLut.Width, HilbertLut.Width, 0x822E, (IntPtr)data, 4);
            }
        }
        if (_output != 0 && _width == width && _height == height) return true;

        ReleaseTargets();
        _workingDepth = _device.CreateStorageTexture((int)width, (int)height, Format.R32Sfloat, DepthLevels);
        _workingTerm = _device.CreateStorageTexture((int)width, (int)height, Format.R8Unorm);
        _edges = _device.CreateStorageTexture((int)width, (int)height, Format.R8Unorm);
        _scratchA = _device.CreateStorageTexture((int)width, (int)height, Format.R8Unorm);
        _scratchB = _device.CreateStorageTexture((int)width, (int)height, Format.R8Unorm);
        _output = _device.CreateStorageTexture((int)width, (int)height, Format.R8Unorm);
        _width = width;
        _height = height;
        return _hilbert != 0 && _device.TextureOf(_workingDepth)?.MipLevels == DepthLevels;
    }

    private bool EnsurePrograms()
    {
        Format depthFormat = _device.TextureOf(_workingDepth)!.Format;
        Format termFormat = _device.TextureOf(_workingTerm)!.Format;
        if (_prefilter != 0 && depthFormat == _programDepthFormat && termFormat == _programTermFormat) return true;
        ReleasePrograms();

        _prefilter = _device.CreateComputeProgram(GtaoShaderSources.Build("prefilter.comp", depthFormat, termFormat),
            "gtao-prefilter", Slots(ComputeSlotKind.Sampled, 1, ComputeSlotKind.Storage, DepthLevels),
            GtaoSettings.PushConstantBytes);
        _main = _device.CreateComputeProgram(GtaoShaderSources.Build("main.comp", depthFormat, termFormat),
            "gtao-main", Slots(ComputeSlotKind.Sampled, 4, ComputeSlotKind.Storage, 2), GtaoSettings.PushConstantBytes);
        _denoise = _device.CreateComputeProgram(GtaoShaderSources.Build("denoise.comp", depthFormat, termFormat),
            "gtao-denoise", Slots(ComputeSlotKind.Sampled, 4, ComputeSlotKind.Storage, 1), GtaoSettings.PushConstantBytes);
        _programDepthFormat = depthFormat;
        _programTermFormat = termFormat;
        if (_prefilter != 0 && _main != 0 && _denoise != 0) return true;

        LastError = "AO compute shaders failed to compile: " + _device.GetError();
        ProgramsFailed = true;
        ReleasePrograms();
        return false;
    }

    /// <summary>Sampled bindings first, then storage bindings, numbered from 0.</summary>
    private static ComputeSlot[] Slots(ComputeSlotKind first, int firstCount, ComputeSlotKind second, int secondCount)
    {
        var slots = new ComputeSlot[firstCount + secondCount];
        for (int i = 0; i < slots.Length; i++) slots[i] = new ComputeSlot((uint)i, i < firstCount ? first : second);
        return slots;
    }

    /// <summary>Frees the size-dependent targets (a framebuffer rebuild); the next render recreates them.</summary>
    public void ReleaseTargets()
    {
        foreach (int texture in new[] { _workingDepth, _workingTerm, _edges, _scratchA, _scratchB, _output })
        {
            if (texture != 0) _device.DeleteTexture(texture);
        }
        _workingDepth = _workingTerm = _edges = _scratchA = _scratchB = _output = 0;
        _width = _height = 0;
    }

    private void ReleasePrograms()
    {
        if (_prefilter != 0) _device.DeleteComputeProgram(_prefilter);
        if (_main != 0) _device.DeleteComputeProgram(_main);
        if (_denoise != 0) _device.DeleteComputeProgram(_denoise);
        _prefilter = _main = _denoise = 0;
    }

    public void Dispose()
    {
        ReleaseTargets();
        ReleasePrograms();
        if (_hilbert != 0) _device.DeleteTexture(_hilbert);
        _hilbert = 0;
    }
}

/// <summary>
/// The reconstruction constants of the AO passes from a GL projection
/// (docs/vulkan.md#ambient-occlusion, integration plan step 1): view depth from the
/// [0, 1] depth buffer and view XY from the texel's UV, in the working frame x right,
/// y up, z forward (GL view space mirrored in z). The texel rows run bottom-up (GL order;
/// the device never flips), so unlike XeGTAO's D3D constants the Y terms keep their sign.
///
/// The jitter columns of the projection (P[2][0], P[2][1]) are folded into the offset,
/// so a jittered G-buffer reconstructs exactly rather than within a sub-pixel.
/// </summary>
internal readonly record struct GtaoProjection(
    float DepthUnpackMul, float DepthUnpackAdd,
    float NdcToViewMulX, float NdcToViewMulY,
    float NdcToViewAddX, float NdcToViewAddY)
{
    /// <summary>
    /// From a column-major GL perspective matrix (<c>m[10] = A</c>, <c>m[14] = B</c>,
    /// <c>m[11] = -1</c>); null for anything that is not a perspective projection.
    /// </summary>
    public static GtaoProjection? From(float[]? m)
    {
        if (m == null || m.Length < 16) return null;
        if (MathF.Abs(m[11] + 1f) > 1e-4f || MathF.Abs(m[15]) > 1e-4f) return null;
        if (m[0] == 0f || m[5] == 0f || m[14] == 0f) return null;

        float a = m[10];
        float b = m[14];
        float tanX = 1f / m[0];
        float tanY = 1f / m[5];
        return new GtaoProjection(
            -b / 2f, (1f - a) / 2f,
            2f * tanX, 2f * tanY,
            (m[8] - 1f) * tanX, (m[9] - 1f) * tanY);
    }

    /// <summary>View depth (positive, forward) of a [0, 1] depth value; the shader's <c>gtaoViewDepth</c>.</summary>
    public float ViewDepth(float screenDepth) => DepthUnpackMul / (DepthUnpackAdd - screenDepth);

    /// <summary>The working-frame position of a texel UV (row 0 at the bottom) at a view depth; <c>gtaoViewPosition</c>.</summary>
    public (float X, float Y, float Z) ViewPosition(float u, float v, float viewDepth) =>
        ((NdcToViewMulX * u + NdcToViewAddX) * viewDepth, (NdcToViewMulY * v + NdcToViewAddY) * viewDepth, viewDepth);
}

/// <summary>
/// The AO compute shaders, <c>sources/shaders-vk/gtao/*.comp</c>, embedded in the renderer
/// assembly (so deploy and the packagers carry them with the DLL). Expands their
/// <c>#include "x.glsl"</c> lines from the same directory and defines the storage format
/// qualifiers of the formats the device chose right after <c>#version</c>.
/// </summary>
internal static class GtaoShaderSources
{
    public const string ResourcePrefix = "shaders-vk/gtao/";

    private static readonly ConcurrentDictionary<string, string> Raw = new(StringComparer.Ordinal);
    private static readonly Regex IncludeLine = new("^[ \\t]*#include[ \\t]+\"([^\"]+)\"[ \\t]*\\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>A resource of the gtao directory, as committed.</summary>
    public static string Read(string fileName) => Raw.GetOrAdd(fileName, name =>
    {
        Assembly assembly = typeof(GtaoShaderSources).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourcePrefix + name);
        if (stream == null) throw new FileNotFoundException("embedded AO shader missing: " + ResourcePrefix + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// The compilable source of <paramref name="fileName" />: includes expanded (each file
    /// once) and <c>GTAO_DEPTH_FORMAT</c> / <c>GTAO_TERM_FORMAT</c> defined.
    /// </summary>
    public static string Build(string fileName, Format depthFormat, Format termFormat)
    {
        string source = Expand(Read(fileName), new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal));
        int versionEnd = source.IndexOf('\n', source.IndexOf("#version", StringComparison.Ordinal)) + 1;
        string defines = "#define GTAO_DEPTH_FORMAT " + Qualifier(depthFormat) + "\n" +
                         "#define GTAO_TERM_FORMAT " + Qualifier(termFormat) + "\n";
        return source.Insert(versionEnd, defines);
    }

    private static string Expand(string source, System.Collections.Generic.HashSet<string> included) =>
        IncludeLine.Replace(source, match =>
        {
            string name = match.Groups[1].Value;
            return included.Add(name) ? Expand(Read(name), included) : "";
        });

    /// <summary>The storage image format qualifier of a format a storage texture can have.</summary>
    public static string Qualifier(Format format) => format switch
    {
        Format.R8Unorm => "r8",
        Format.R8G8Unorm => "rg8",
        Format.R8G8B8A8Unorm => "rgba8",
        Format.R16Sfloat => "r16f",
        Format.R32Sfloat => "r32f",
        Format.R16G16B16A16Sfloat => "rgba16f",
        Format.R32G32B32A32Sfloat => "rgba32f",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "no storage qualifier for this format"),
    };
}

/// <summary>
/// The 64x64 Hilbert index table the AO noise starts from (docs/vulkan.md#ambient-occlusion
/// C.7): generated at startup, never shipped. Neighbouring texels get neighbouring indices,
/// so the R2 sequence the index drives is low-discrepancy across the 3x3 denoise
/// footprint. XeGTAO's <c>HilbertIndex</c> (vaGTAO.hlsl, MIT, Intel), level 6.
/// </summary>
internal static class HilbertLut
{
    public const int Width = 64;

    /// <summary>The curve index of texel (<paramref name="x" />, <paramref name="y" />), 0..4095.</summary>
    public static uint Index(uint x, uint y)
    {
        uint index = 0;
        for (uint level = Width / 2; level > 0; level /= 2)
        {
            uint regionX = (x & level) > 0 ? 1u : 0u;
            uint regionY = (y & level) > 0 ? 1u : 0u;
            index += level * level * ((3u * regionX) ^ regionY);
            if (regionY == 0)
            {
                if (regionX == 1)
                {
                    x = Width - 1 - x;
                    y = Width - 1 - y;
                }
                (x, y) = (y, x);
            }
        }
        return index;
    }

    /// <summary>The table in row order as floats, for an R32F texture: every index is exact in a float.</summary>
    public static float[] Build()
    {
        var table = new float[Width * Width];
        for (uint y = 0; y < Width; y++)
        for (uint x = 0; x < Width; x++)
            table[y * Width + x] = Index(x, y);
        return table;
    }
}
