using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// GL answers a read of a vertex attribute the draw does not supply with the
/// current generic attribute, which defaults to (0, 0, 0, 1). Vulkan has no such
/// thing, and the difference is not academic: the GUI quad carries only positions
/// and UVs while gui.vsh declares six inputs, and gui.fsh discards a fragment
/// based on one of the missing ones. Undefined there meant the entire interface
/// rendered as nothing, with no validation message to say why.
/// </summary>
public class VertexAttributeDefaultTests
{
    private static VertexInputSlot Slot(string name, int location, string type)
    {
        Assert.True(GlslType.TryParse(type, out GlslType parsed), "unknown type " + type);
        return new VertexInputSlot(name, location, parsed);
    }

    /// <summary>The GUI quad's own shape: positions at 0, UVs at 1, nothing else.</summary>
    private static VertexLayoutDescription QuadLayout() => new(
        new[]
        {
            new VertexBinding(0, 12, PerInstance: false),
            new VertexBinding(1, 8, PerInstance: false),
        },
        new[]
        {
            new VertexAttribute(0, 0, Format.R32G32B32Sfloat, 0),
            new VertexAttribute(1, 1, Format.R32G32Sfloat, 0),
        });

    [Fact]
    public void MissingAttributesGetABindingOfTheirOwn()
    {
        var declared = new List<VertexInputSlot>
        {
            Slot("vertexPositionIn", 0, "vec3"),
            Slot("uvIn", 1, "vec2"),
            Slot("colorIn", 2, "vec4"),
            Slot("renderFlagsIn", 3, "int"),
            Slot("damageEffectIn", 4, "float"),
            Slot("jointId", 5, "int"),
        };

        VertexLayoutDescription merged = QuadLayout().WithDefaultsFor(declared);

        Assert.Equal(6, merged.Attributes.Length);
        Assert.Equal(3, merged.Bindings.Length);
        Assert.Equal(VertexLayoutDescription.DefaultAttributeBinding, merged.Bindings[^1].Binding);

        // Stride zero is what makes every vertex read the same constant.
        Assert.Equal(0u, merged.Bindings[^1].Stride);
        Assert.False(merged.Bindings[^1].PerInstance);
    }

    [Fact]
    public void SuppliedAttributesAreLeftOnTheirOwnBinding()
    {
        var declared = new List<VertexInputSlot>
        {
            Slot("vertexPositionIn", 0, "vec3"),
            Slot("uvIn", 1, "vec2"),
            Slot("colorIn", 2, "vec4"),
        };

        VertexLayoutDescription merged = QuadLayout().WithDefaultsFor(declared);

        VertexAttribute position = merged.Attributes.Single(a => a.Location == 0);
        VertexAttribute uv = merged.Attributes.Single(a => a.Location == 1);
        Assert.Equal(0u, position.Binding);
        Assert.Equal(1u, uv.Binding);

        VertexAttribute color = merged.Attributes.Single(a => a.Location == 2);
        Assert.Equal(VertexLayoutDescription.DefaultAttributeBinding, color.Binding);
    }

    /// <summary>
    /// Integer attributes have to read integer zeros, so they take the second
    /// half of the defaults buffer rather than reinterpreting float bits.
    /// </summary>
    [Theory]
    [InlineData("float", Format.R32Sfloat, 0u)]
    [InlineData("vec2", Format.R32G32Sfloat, 0u)]
    [InlineData("vec3", Format.R32G32B32Sfloat, 0u)]
    [InlineData("vec4", Format.R32G32B32A32Sfloat, 0u)]
    [InlineData("int", Format.R32Sint, 16u)]
    [InlineData("ivec4", Format.R32G32B32A32Sint, 16u)]
    [InlineData("uint", Format.R32Sint, 16u)]
    public void DefaultsUseTheFormatAndHalfMatchingTheDeclaredType(
        string type, Format expectedFormat, uint expectedOffset)
    {
        VertexLayoutDescription merged =
            VertexLayoutDescription.Empty.WithDefaultsFor(new[] { Slot("x", 7, type) });

        VertexAttribute attribute = Assert.Single(merged.Attributes);
        Assert.Equal(7u, attribute.Location);
        Assert.Equal(expectedFormat, attribute.Format);
        Assert.Equal(expectedOffset, attribute.Offset);
    }

    /// <summary>
    /// A layout that already covers everything must come back untouched, so no
    /// pipeline gains a binding it will never have a buffer for.
    /// </summary>
    [Fact]
    public void NothingIsAddedWhenTheMeshCoversEveryDeclaredInput()
    {
        var declared = new List<VertexInputSlot>
        {
            Slot("vertexPositionIn", 0, "vec3"),
            Slot("uvIn", 1, "vec2"),
        };

        VertexLayoutDescription layout = QuadLayout();
        VertexLayoutDescription merged = layout.WithDefaultsFor(declared);

        Assert.Same(layout, merged);
    }

    /// <summary>
    /// A program declaring no inputs at all - the fullscreen passes - must not
    /// grow a binding either.
    /// </summary>
    [Fact]
    public void NothingIsAddedForAProgramWithNoDeclaredInputs()
    {
        VertexLayoutDescription layout = QuadLayout();
        Assert.Same(layout, layout.WithDefaultsFor(Array.Empty<VertexInputSlot>()));
    }

    /// <summary>
    /// The layout the parser produces for gui.vsh is the case this all exists
    /// for, so it is pinned against the real shader's declarations rather than a
    /// hand-written list.
    /// </summary>
    [Fact]
    public void TheGuiShaderDeclaresEveryInputItReads()
    {
        const string source = """
            #version 330 core
            layout(location = 0) in vec3 vertexPositionIn;
            layout(location = 1) in vec2 uvIn;
            layout(location = 2) in vec4 colorIn;
            layout(location = 3) in int renderFlagsIn;
            layout(location = 4) in float damageEffectIn;
            layout(location = 5) in int jointId;
            void main() { gl_Position = vec4(vertexPositionIn, 1.0); }
            """;

        ProgramInterfaceLayout layout = ProgramInterfaceLayout.Build(
            new[] { (EnumShaderType.VertexShader, GlslParser.Parse(source)) });

        Assert.Equal(6, layout.VertexInputs.Count);
        Assert.Contains(layout.VertexInputs, s => s.Name == "damageEffectIn" && s.Location == 4);
        Assert.Contains(layout.VertexInputs, s => s.Name == "renderFlagsIn" && s.Location == 3);
    }

    /// <summary>
    /// Sampler locations have to be tellable apart from uniform block offsets,
    /// which start at zero, and from GL's "not found" answer of -1 - otherwise
    /// assigning a sampler its texture unit would write into the uniform block
    /// at some arbitrary offset instead.
    /// </summary>
    [Theory]
    [InlineData(-2, true)]
    [InlineData(-3, true)]
    [InlineData(-100, true)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(36, false)]
    public void SamplerLocationsAreDisjointFromBlockOffsets(int location, bool isSampler)
    {
        Assert.Equal(isSampler, ShaderProgramResources.IsSamplerLocation(location));
    }

    /// <summary>
    /// Asking for Vulkan by name gets it wherever it runs; the automatic setting
    /// is a default nobody chose, so it only selects the backend on drivers the
    /// backend has been exercised against.
    /// </summary>
    [SkippableFact]
    public void AnExplicitRendererChoiceIgnoresTheAutomaticAllowList()
    {
        Skip.IfNot(VulkanDevice.IsSupported(out string? probeReason), probeReason ?? "No usable Vulkan device.");

        // Explicit: allowed on whatever this machine has.
        Assert.True(VulkanDevice.IsSupported(false, out _, out string driver));
        Assert.False(string.IsNullOrWhiteSpace(driver));

        // Automatic: allowed too, because both development drivers are on the
        // list. If this ever fails the machine grew a driver worth adding.
        Assert.True(VulkanDevice.IsSupported(true, out string automaticReason, out _),
            "automatic selection refused driver '" + driver + "': " + automaticReason);
    }

    /// <summary>
    /// The allow-list itself, which is the part with the logic. Only the drivers
    /// the backend is actually run against may be picked automatically; anything
    /// unrecognised stays on OpenGL rather than becoming the first person to try
    /// the backend on that driver.
    /// </summary>
    [Theory]
    [InlineData("NVIDIA", true)]
    [InlineData("Intel open-source Mesa driver", true)]
    [InlineData("Intel Corporation", true)]
    [InlineData("radv", true)]
    [InlineData("AMD proprietary driver", true)]
    [InlineData("Mesa llvmpipe", true)]
    [InlineData("SwiftShader", false)]
    [InlineData("MoltenVK", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void AutomaticSelectionOnlyTakesKnownDrivers(string driverName, bool allowed)
    {
        Assert.Equal(allowed, VulkanDevice.IsAllowedForAutomaticSelection(driverName));
    }
}
