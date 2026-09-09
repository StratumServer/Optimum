using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>One vertex buffer feeding the pipeline.</summary>
internal readonly record struct VertexBinding(uint Binding, uint Stride, bool PerInstance);

/// <summary>One vertex attribute read out of a binding.</summary>
internal readonly record struct VertexAttribute(uint Location, uint Binding, Format Format, uint Offset);

/// <summary>
/// The vertex input state of a mesh.
///
/// Vintage Story gives every attribute its own buffer rather than interleaving
/// one - separate allocations for positions, normals, UVs, colours, flags, plus
/// up to four custom parts that <em>are</em> interleaved and may be per-instance.
/// So a binding here is usually one buffer with one attribute, and the custom
/// parts are the exception.
///
/// There are on the order of fifteen distinct layouts in the whole game, so they
/// are interned and the id goes into the pipeline key.
/// </summary>
internal sealed class VertexLayoutDescription : IEquatable<VertexLayoutDescription>
{
    public VertexBinding[] Bindings { get; }
    public VertexAttribute[] Attributes { get; }

    private readonly int _hash;

    public VertexLayoutDescription(VertexBinding[] bindings, VertexAttribute[] attributes)
    {
        Bindings = bindings;
        Attributes = attributes;

        var hash = new HashCode();
        foreach (VertexBinding binding in bindings) hash.Add(binding);
        foreach (VertexAttribute attribute in attributes) hash.Add(attribute);
        _hash = hash.ToHashCode();
    }

    /// <summary>The layout of a pass that generates its vertices in the shader.</summary>
    public static VertexLayoutDescription Empty { get; } =
        new(Array.Empty<VertexBinding>(), Array.Empty<VertexAttribute>());

    /// <summary>
    /// The binding the constant-default attribute buffer occupies.
    ///
    /// Meshes number their bindings from zero and never approach this, and the
    /// Vulkan minimum for maxVertexInputBindings is 16, so 15 is always available
    /// and never collides.
    /// </summary>
    public const uint DefaultAttributeBinding = 15;

    /// <summary>
    /// Adds constant-default attributes for every location the program declares
    /// but this layout does not provide.
    ///
    /// GL answers a read of an unbound vertex attribute with the current generic
    /// attribute, which defaults to (0, 0, 0, 1); Vulkan leaves it undefined.
    /// That difference is not cosmetic - gui.fsh discards a fragment based on a
    /// damage effect fed by an attribute the GUI quad never carries, so undefined
    /// there means the entire interface vanishes with no validation message.
    /// </summary>
    public VertexLayoutDescription WithDefaultsFor(IReadOnlyList<VertexInputSlot> declared)
    {
        List<VertexAttribute>? added = null;
        foreach (VertexInputSlot slot in declared)
        {
            bool present = false;
            foreach (VertexAttribute attribute in Attributes)
            {
                if (attribute.Location == (uint)slot.Location) { present = true; break; }
            }
            if (present) continue;

            added ??= new List<VertexAttribute>();
            added.Add(new VertexAttribute(
                (uint)slot.Location, DefaultAttributeBinding,
                DefaultFormatFor(slot.Type), DefaultOffsetFor(slot.Type)));
        }

        if (added == null) return this;

        var bindings = new VertexBinding[Bindings.Length + 1];
        Array.Copy(Bindings, bindings, Bindings.Length);
        // Stride zero: every vertex reads the same constant.
        bindings[^1] = new VertexBinding(DefaultAttributeBinding, 0, PerInstance: false);

        var attributes = new VertexAttribute[Attributes.Length + added.Count];
        Array.Copy(Attributes, attributes, Attributes.Length);
        for (int i = 0; i < added.Count; i++) attributes[Attributes.Length + i] = added[i];

        return new VertexLayoutDescription(bindings, attributes);
    }

    /// <summary>
    /// Integer attributes have to read integer zeros and floats floating-point
    /// ones, so the default buffer holds both and the type picks the half.
    /// </summary>
    private static uint DefaultOffsetFor(GlslType type) => IsIntegerType(type) ? 16u : 0u;

    private static Format DefaultFormatFor(GlslType type)
    {
        bool integer = IsIntegerType(type);
        return type.ComponentCount switch
        {
            1 => integer ? Format.R32Sint : Format.R32Sfloat,
            2 => integer ? Format.R32G32Sint : Format.R32G32Sfloat,
            3 => integer ? Format.R32G32B32Sint : Format.R32G32B32Sfloat,
            _ => integer ? Format.R32G32B32A32Sint : Format.R32G32B32A32Sfloat,
        };
    }

    private static bool IsIntegerType(GlslType type) =>
        type.Name.StartsWith("i", StringComparison.Ordinal) ||
        type.Name.StartsWith("u", StringComparison.Ordinal) ||
        type.Name == "int" || type.Name == "uint" || type.Name == "bool";

    public bool Equals(VertexLayoutDescription? other)
    {
        if (other is null || other._hash != _hash) return false;
        if (Bindings.Length != other.Bindings.Length) return false;
        if (Attributes.Length != other.Attributes.Length) return false;

        for (int i = 0; i < Bindings.Length; i++)
        {
            if (!Bindings[i].Equals(other.Bindings[i])) return false;
        }
        for (int i = 0; i < Attributes.Length; i++)
        {
            if (!Attributes[i].Equals(other.Attributes[i])) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as VertexLayoutDescription);
    public override int GetHashCode() => _hash;
}

/// <summary>
/// Builds a vertex layout the way the game's mesh allocator describes one.
///
/// The GL path calls glVertexAttribPointer per attribute with a raw type
/// constant, so the mapping from those constants to Vulkan formats is the whole
/// job. Getting one wrong shows up as garbled geometry rather than an error,
/// which is why the mapping is tested rather than trusted.
/// </summary>
internal sealed class VertexLayoutBuilder
{
    private readonly List<VertexBinding> _bindings = new();
    private readonly List<VertexAttribute> _attributes = new();
    private uint _nextLocation;

    /// <summary>
    /// Adds an attribute backed by its own tightly packed buffer, which is how
    /// positions, normals, UVs, colours and flags arrive.
    /// </summary>
    public VertexLayoutBuilder AddDedicated(Format format, uint stride, bool perInstance = false)
    {
        uint binding = (uint)_bindings.Count;
        _bindings.Add(new VertexBinding(binding, stride, perInstance));
        _attributes.Add(new VertexAttribute(_nextLocation++, binding, format, 0));
        return this;
    }

    /// <summary>
    /// Adds an interleaved group sharing one buffer, which is how the custom
    /// mesh data parts arrive - several attributes at different offsets within a
    /// common stride, optionally advancing per instance.
    /// </summary>
    public VertexLayoutBuilder AddInterleaved(
        ReadOnlySpan<(Format Format, uint Offset)> members, uint stride, bool perInstance)
    {
        uint binding = (uint)_bindings.Count;
        _bindings.Add(new VertexBinding(binding, stride, perInstance));
        foreach ((Format format, uint offset) in members)
        {
            _attributes.Add(new VertexAttribute(_nextLocation++, binding, format, offset));
        }
        return this;
    }

    public VertexLayoutDescription Build() => new(_bindings.ToArray(), _attributes.ToArray());

    // ------------------------------------------------------------ format mapping

    private const int GlByte = 0x1400;
    private const int GlUnsignedByte = 0x1401;
    private const int GlShort = 0x1402;
    private const int GlUnsignedShort = 0x1403;
    private const int GlInt = 0x1404;
    private const int GlUnsignedInt = 0x1405;
    private const int GlFloat = 0x1406;
    private const int GlInt2101010Rev = 0x8D9F;

    /// <summary>
    /// Maps a glVertexAttribPointer description to a Vulkan format.
    /// </summary>
    /// <param name="components">1 to 4, or 4 for a packed 2-10-10-10 normal.</param>
    /// <param name="glType">The GL type constant.</param>
    /// <param name="normalized">
    /// True when GL would scale integers into [0,1] or [-1,1]. False with
    /// <paramref name="integer" /> false means the shader sees the raw value as a
    /// float; true with integer means an integer attribute.
    /// </param>
    /// <param name="integer">True for glVertexAttribIPointer.</param>
    public static Format FormatFor(int components, int glType, bool normalized, bool integer)
    {
        // The packed normal format the game uses for entity and particle normals.
        if (glType == GlInt2101010Rev)
        {
            return normalized ? Format.A2B10G10R10SNormPack32 : Format.A2B10G10R10SintPack32;
        }

        return glType switch
        {
            GlFloat => components switch
            {
                1 => Format.R32Sfloat,
                2 => Format.R32G32Sfloat,
                3 => Format.R32G32B32Sfloat,
                _ => Format.R32G32B32A32Sfloat,
            },
            GlUnsignedByte => Select(components, normalized, integer,
                (Format.R8Unorm, Format.R8G8Unorm, Format.R8G8B8Unorm, Format.R8G8B8A8Unorm),
                (Format.R8Uint, Format.R8G8Uint, Format.R8G8B8Uint, Format.R8G8B8A8Uint),
                (Format.R8Uscaled, Format.R8G8Uscaled, Format.R8G8B8Uscaled, Format.R8G8B8A8Uscaled)),
            GlByte => Select(components, normalized, integer,
                (Format.R8SNorm, Format.R8G8SNorm, Format.R8G8B8SNorm, Format.R8G8B8A8SNorm),
                (Format.R8Sint, Format.R8G8Sint, Format.R8G8B8Sint, Format.R8G8B8A8Sint),
                (Format.R8Sscaled, Format.R8G8Sscaled, Format.R8G8B8Sscaled, Format.R8G8B8A8Sscaled)),
            GlUnsignedShort => Select(components, normalized, integer,
                (Format.R16Unorm, Format.R16G16Unorm, Format.R16G16B16Unorm, Format.R16G16B16A16Unorm),
                (Format.R16Uint, Format.R16G16Uint, Format.R16G16B16Uint, Format.R16G16B16A16Uint),
                (Format.R16Uscaled, Format.R16G16Uscaled, Format.R16G16B16Uscaled, Format.R16G16B16A16Uscaled)),
            GlShort => Select(components, normalized, integer,
                (Format.R16SNorm, Format.R16G16SNorm, Format.R16G16B16SNorm, Format.R16G16B16A16SNorm),
                (Format.R16Sint, Format.R16G16Sint, Format.R16G16B16Sint, Format.R16G16B16A16Sint),
                (Format.R16Sscaled, Format.R16G16Sscaled, Format.R16G16B16Sscaled, Format.R16G16B16A16Sscaled)),
            GlUnsignedInt => components switch
            {
                1 => Format.R32Uint,
                2 => Format.R32G32Uint,
                3 => Format.R32G32B32Uint,
                _ => Format.R32G32B32A32Uint,
            },
            GlInt => components switch
            {
                1 => Format.R32Sint,
                2 => Format.R32G32Sint,
                3 => Format.R32G32B32Sint,
                _ => Format.R32G32B32A32Sint,
            },
            _ => Format.R32G32B32A32Sfloat,
        };
    }

    private static Format Select(
        int components, bool normalized, bool integer,
        (Format, Format, Format, Format) norm,
        (Format, Format, Format, Format) asInteger,
        (Format, Format, Format, Format) scaled)
    {
        (Format one, Format two, Format three, Format four) =
            integer ? asInteger : normalized ? norm : scaled;

        return components switch { 1 => one, 2 => two, 3 => three, _ => four };
    }

    /// <summary>Bytes one vertex of this format occupies.</summary>
    public static uint SizeOf(int components, int glType)
    {
        if (glType == GlInt2101010Rev) return 4;

        uint componentSize = glType switch
        {
            GlByte or GlUnsignedByte => 1u,
            GlShort or GlUnsignedShort => 2u,
            _ => 4u,
        };
        return componentSize * (uint)Math.Clamp(components, 1, 4);
    }
}
