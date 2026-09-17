using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// The layout of a program linked from the native manifest (docs/vulkan-native-shaders.md section 8).
///
/// The draw path reads the same <see cref="ProgramInterfaceLayout" /> whichever way a program was
/// linked, so a native program is described in the rewriter's terms: frame members through the owner
/// rule (the manifest's <c>frameMembers</c>), record members at their reflected offsets, sampler slots
/// at their push offsets, and set 0 frame textures under their game names. The one thing the rewriter
/// never produces is a push member that is not a sampler slot (a DRAW uniform, section 4); those live in
/// <see cref="PushMembers" /> and are written through the push location range.
/// </summary>
internal sealed partial class ProgramInterfaceLayout
{
    /// <summary>Push-block members that are not sampler slots, in block order. Empty for a rewritten program.</summary>
    public List<UniformMember> PushMembers { get; } = new();
    public Dictionary<string, UniformMember> PushMembersByName { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The per-program push shadow a native program's non-slot push members persist in, seeded with the
    /// GLSL 330 initializers; null when the push block holds only sampler slots (every rewritten program),
    /// because the draw rewrites every slot anyway.
    /// </summary>
    public byte[]? CreatePushShadow()
    {
        if (PushMembers.Count == 0) return null;
        var buffer = new byte[PushConstantSize];
        foreach (UniformMember member in PushMembers)
        {
            if (member.Initializer != null) WriteInitializer(buffer, member);
        }
        return buffer;
    }

    /// <summary>
    /// Builds the layout of one manifest variant. <paramref name="oracle" /> is what the GLSL 330 source the
    /// program still carries says: sampler units in <c>collectUniformNames</c> order and the declarations'
    /// initializers, neither of which the manifest records.
    /// </summary>
    public static ProgramInterfaceLayout FromNative(NativeVariant variant, GlslUniformOracle oracle)
    {
        var layout = new ProgramInterfaceLayout();

        foreach (string name in variant.FrameMembers)
        {
            if (!FrameGlobals.TryGetMember(name, out UniformMember frame))
            {
                layout.Errors.Add("frame member '" + name + "' is not in FrameGlobals");
                continue;
            }
            layout.FrameMemberDeclaredLengths[name] = frame.ArrayLength;
        }

        if (variant.Record != null)
        {
            foreach (NativeMember member in variant.Record.Members)
            {
                UniformMember? built = MemberOf(layout, member, oracle, "record");
                if (built == null) continue;
                layout.Members.Add(built);
                layout.MembersByName[built.Name] = built;
            }
            layout.BlockSize = variant.Record.Size;
        }

        var slots = new Dictionary<string, NativeSampler>(StringComparer.Ordinal);
        foreach (NativeSampler sampler in variant.Samplers) slots[sampler.Name] = sampler;

        if (variant.Push != null)
        {
            if (variant.Push.Size > SetConvention.PushConstantBytes)
            {
                layout.Errors.Add("push block of " + variant.Push.Size + " B is past the " + SetConvention.PushConstantBytes + " B the shared layout holds");
            }
            foreach (NativeMember member in variant.Push.Members)
            {
                if (slots.ContainsKey(member.Name)) continue;
                UniformMember? built = MemberOf(layout, member, oracle, "push");
                if (built == null) continue;
                layout.PushMembers.Add(built);
                layout.PushMembersByName[built.Name] = built;
            }
            layout.PushConstantSize = variant.Push.Size;
        }

        AddNativeSamplers(layout, variant, slots, oracle);

        foreach (NativeStorageBinding binding in variant.StorageBindings)
        {
            if (!binding.Used) continue;
            if (binding.Set != SetConvention.StorageSet)
            {
                layout.Errors.Add("storage binding '" + binding.Name + "' is in set " + binding.Set);
                continue;
            }
            // The client names its animation UBOs "Animation" and "AnimationPrev"; the device feeds a block
            // binding from the UBO bound under the block's name, so the convention's binding decides the name.
            switch (binding.Binding)
            {
                case SetConvention.FaceDataBinding:
                    layout.StorageBlocks.Add(new BlockBinding { BlockName = binding.Name, Binding = binding.Binding });
                    break;
                case SetConvention.AnimationBinding:
                    layout.UniformBlocks.Add(new BlockBinding { BlockName = "Animation", Binding = binding.Binding });
                    break;
                case SetConvention.AnimationPrevBinding:
                    layout.UniformBlocks.Add(new BlockBinding { BlockName = "AnimationPrev", Binding = binding.Binding });
                    break;
                default:
                    layout.Errors.Add("storage binding '" + binding.Name + "' at binding " + binding.Binding +
                                      " has no native feed (the named-block range is the rewriter's)");
                    break;
            }
        }

        foreach (NativeInterfaceVariable input in variant.VertexInputs)
        {
            if (input.ArrayLength != 0 || !GlslType.TryParse(input.Type, out GlslType type) || type.IsMatrix || type.IsOpaque) continue;
            layout.VertexInputLocations[input.Name] = input.Location;
            RecordNativeVertexInput(layout, new VertexInputSlot(input.Name, input.Location, type));
        }

        foreach (NativeInterfaceVariable output in variant.FragmentOutputs)
        {
            layout.FragmentOutputLocations[output.Name] = output.Location;
        }
        for (int bit = 0; bit < 32; bit++)
        {
            if ((variant.WrittenOutputs & (1u << bit)) != 0) layout.WrittenFragmentOutputs.Add(bit);
        }

        return layout;
    }

    private static void RecordNativeVertexInput(ProgramInterfaceLayout layout, VertexInputSlot slot)
    {
        foreach (VertexInputSlot existing in layout.VertexInputs)
        {
            if (existing.Location == slot.Location) return;
        }
        layout.VertexInputs.Add(slot);
    }

    private static UniformMember? MemberOf(ProgramInterfaceLayout layout, NativeMember member, GlslUniformOracle oracle, string block)
    {
        if (!GlslType.TryParse(member.Type, out GlslType type))
        {
            layout.Errors.Add(block + " member '" + member.Name + "' has type '" + member.Type + "', which the device does not model");
            return null;
        }
        if (member.ArrayLength < 0)
        {
            layout.Errors.Add(block + " member '" + member.Name + "' is a runtime array");
            return null;
        }
        return new UniformMember
        {
            Name = member.Name,
            Type = type,
            ArrayLength = member.ArrayLength,
            Offset = member.Offset,
            Size = member.Size,
            Initializer = oracle.InitializerOf(member.Name),
        };
    }

    /// <summary>
    /// The sampler list in the texture-unit order the client assigns (<c>collectUniformNames</c> over the
    /// GLSL 330 text): a name that is a set 0 frame texture reads that binding, a manifest slot takes its
    /// push offset, and a name the oracle sees but neither side declares (its <c>Array</c> quirk) still
    /// consumes its unit. A slot the oracle cannot see (a type outside its list) follows, in push order.
    /// </summary>
    private static void AddNativeSamplers(
        ProgramInterfaceLayout layout, NativeVariant variant, Dictionary<string, NativeSampler> slots, GlslUniformOracle oracle)
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        int nextUnit = 0;
        foreach ((string name, string typeName, int unit) in oracle.SamplersByUnit())
        {
            nextUnit = Math.Max(nextUnit, unit + 1);
            if (!placed.Add(name)) continue;

            if (slots.TryGetValue(name, out NativeSampler? slot))
            {
                AddSlot(layout, slot, unit);
                continue;
            }
            foreach (SetConvention.Binding frame in SetConvention.FrameTextures)
            {
                if (string.Equals(frame.Name, name, StringComparison.Ordinal) &&
                    string.Equals(frame.GlslType, typeName, StringComparison.Ordinal))
                {
                    AddFrameTexture(layout, name, typeName, frame.Value, unit);
                    break;
                }
            }
        }

        foreach (NativeSampler slot in variant.Samplers)
        {
            if (placed.Add(slot.Name)) AddSlot(layout, slot, nextUnit++);
        }
        foreach (NativeFrameTexture texture in variant.FrameTextures)
        {
            if (placed.Add(texture.Name)) AddFrameTexture(layout, texture.Name, texture.GlslType, texture.Binding, nextUnit++);
        }

        layout.Samplers.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    private static void AddSlot(ProgramInterfaceLayout layout, NativeSampler slot, int unit)
    {
        if (!BindlessKinds.TryFromGlslType(slot.GlslType, out TextureKind kind))
        {
            layout.Errors.Add("sampler '" + slot.Name + "' has type '" + slot.GlslType + "', for which set 1 has no bindless array");
            return;
        }
        var binding = new SamplerBinding
        {
            Name = slot.Name,
            TypeName = slot.GlslType,
            Order = unit,
            Kind = kind,
            PushOffset = slot.PushOffset,
        };
        layout.Samplers.Add(binding);
        layout.SamplersByName[binding.Name] = binding;
    }

    private static void AddFrameTexture(ProgramInterfaceLayout layout, string name, string typeName, int frameBinding, int unit)
    {
        var binding = new SamplerBinding { Name = name, TypeName = typeName, Order = unit, FrameBinding = frameBinding };
        layout.Samplers.Add(binding);
        layout.SamplersByName[name] = binding;
        layout.UsesFrameTextures = true;
    }
}
