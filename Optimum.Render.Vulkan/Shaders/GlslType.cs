using System;
using System.Collections.Generic;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// The GLSL types that can appear in a uniform declaration, with their scalar
/// block layout sizes.
///
/// Scalar layout (GL_EXT_scalar_block_layout) aligns every aggregate to its
/// component's natural alignment rather than rounding up to 16 bytes. For the
/// 32-bit types this game uses that means alignment is always 4 and size is
/// simply the component count times 4 - which is exactly how a tightly packed
/// <c>float[]</c> is laid out on the CPU.
///
/// That equivalence is the whole reason this backend asks for scalar layout.
/// The game feeds uniforms as raw arrays: <c>Uniforms3("pointLights", count,
/// float[])</c> sends <c>count * 3</c> floats with no padding, and
/// <c>UniformMatrices4x3</c> sends 12 floats per matrix. Under std140 a
/// <c>vec3[]</c> has a 16-byte stride and a <c>mat4x3[]</c> a 64-byte one, so
/// every array upload would need re-striding on the CPU. Under scalar layout the
/// setter is a memcpy at a recorded offset.
/// </summary>
internal readonly struct GlslType : IEquatable<GlslType>
{
    /// <summary>The name as written in the shader, e.g. "vec3", "mat4x3".</summary>
    public string Name { get; }

    /// <summary>Columns for a matrix; 1 for scalars and vectors.</summary>
    public int Columns { get; }

    /// <summary>Components per column: 3 for vec3 and for a column of mat4x3.</summary>
    public int Rows { get; }

    /// <summary>Bytes per scalar component. 4 for every type this game uses.</summary>
    public int ScalarSize { get; }

    /// <summary>
    /// True for samplers and images: opaque handles that live in descriptors, not
    /// in the uniform block.
    /// </summary>
    public bool IsOpaque { get; }

    private GlslType(string name, int columns, int rows, int scalarSize, bool isOpaque)
    {
        Name = name;
        Columns = columns;
        Rows = rows;
        ScalarSize = scalarSize;
        IsOpaque = isOpaque;
    }

    /// <summary>Total components, e.g. 12 for mat4x3.</summary>
    public int ComponentCount => Columns * Rows;

    /// <summary>Size of one element in bytes under scalar layout.</summary>
    public int Size => ComponentCount * ScalarSize;

    /// <summary>
    /// Alignment under scalar layout: the component's own alignment, never
    /// rounded up. This is what keeps array strides tight.
    /// </summary>
    public int Alignment => ScalarSize;

    public bool IsMatrix => Columns > 1;

    public bool Equals(GlslType other) => Name == other.Name;
    public override bool Equals(object? obj) => obj is GlslType other && Equals(other);
    public override int GetHashCode() => Name?.GetHashCode(StringComparison.Ordinal) ?? 0;
    public override string ToString() => Name;

    private static GlslType Numeric(string name, int columns, int rows) =>
        new(name, columns, rows, 4, isOpaque: false);

    private static GlslType Opaque(string name) =>
        new(name, 1, 1, 0, isOpaque: true);

    private static readonly Dictionary<string, GlslType> ByName = BuildTable();

    private static Dictionary<string, GlslType> BuildTable()
    {
        var table = new Dictionary<string, GlslType>(StringComparer.Ordinal);

        void Add(GlslType type) => table[type.Name] = type;

        // Scalars. bool is 4 bytes in a uniform block, as in GL.
        Add(Numeric("float", 1, 1));
        Add(Numeric("int", 1, 1));
        Add(Numeric("uint", 1, 1));
        Add(Numeric("bool", 1, 1));

        // Vectors.
        for (int n = 2; n <= 4; n++)
        {
            Add(Numeric("vec" + n, 1, n));
            Add(Numeric("ivec" + n, 1, n));
            Add(Numeric("uvec" + n, 1, n));
            Add(Numeric("bvec" + n, 1, n));
        }

        // Matrices. GLSL matCxR is C columns of R rows, and matN is matNxN.
        for (int columns = 2; columns <= 4; columns++)
        {
            Add(Numeric("mat" + columns, columns, columns));
            for (int rows = 2; rows <= 4; rows++)
            {
                Add(Numeric($"mat{columns}x{rows}", columns, rows));
            }
        }

        // Opaque handles. Only the ones the game and its shaders actually use,
        // plus the obvious neighbours so a mod shader is not rejected for using
        // a sampler type this list happened to omit.
        foreach (string sampler in new[]
        {
            "sampler1D", "sampler2D", "sampler3D", "samplerCube",
            "sampler2DShadow", "sampler1DShadow", "samplerCubeShadow",
            "sampler2DArray", "sampler2DArrayShadow", "sampler1DArray",
            "sampler2DMS", "sampler2DMSArray", "samplerBuffer",
            "isampler2D", "isampler3D", "isamplerCube", "isampler2DArray",
            "usampler2D", "usampler3D", "usamplerCube", "usampler2DArray",
            "image2D", "image3D", "imageCube", "image2DArray",
        })
        {
            Add(Opaque(sampler));
        }

        return table;
    }

    /// <summary>
    /// Resolves a type name. Returns false for anything unrecognised - a struct,
    /// or a type this table does not model - which the rewriter treats as a
    /// declaration to leave alone rather than an error.
    /// </summary>
    public static bool TryParse(string name, out GlslType type) => ByName.TryGetValue(name, out type);

    /// <summary>True for a name that is a sampler or image handle.</summary>
    public static bool IsOpaqueTypeName(string name) =>
        ByName.TryGetValue(name, out GlslType type) && type.IsOpaque;
}
