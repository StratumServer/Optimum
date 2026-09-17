using System;
using System.Collections.Generic;
using System.Text;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The few SPIR-V facts the native-shader tests check - names, decorations, struct members,
/// variables and specialization constants - read straight from the word stream. No reflection
/// library exists in the tree (docs/vulkan-native-shaders.md section 6 plans
/// <c>Shaders/SpirvReflection.cs</c>); until it does, this is deliberately minimal.
/// </summary>
internal sealed class SpirvReader
{
    private const uint Magic = 0x07230203;

    private const ushort OpName = 5;
    private const ushort OpMemberName = 6;
    private const ushort OpTypeInt = 21;
    private const ushort OpTypeFloat = 22;
    private const ushort OpTypeArray = 28;
    private const ushort OpTypeStruct = 30;
    private const ushort OpTypePointer = 32;
    private const ushort OpSpecConstant = 50;
    private const ushort OpVariable = 59;
    private const ushort OpDecorate = 71;
    private const ushort OpMemberDecorate = 72;

    public const uint DecorationSpecId = 1;
    public const uint DecorationArrayStride = 6;
    public const uint DecorationBinding = 33;
    public const uint DecorationDescriptorSet = 34;
    public const uint DecorationOffset = 35;

    public const uint StorageUniform = 2;
    public const uint StoragePushConstant = 9;

    public readonly Dictionary<uint, string> Names = new();
    public readonly Dictionary<(uint Type, uint Member), string> MemberNames = new();
    public readonly Dictionary<uint, Dictionary<uint, uint>> Decorations = new();
    public readonly Dictionary<(uint Type, uint Member), Dictionary<uint, uint>> MemberDecorations = new();
    public readonly Dictionary<uint, uint[]> Structs = new();
    public readonly Dictionary<uint, uint> ArrayElements = new();
    public readonly Dictionary<uint, (uint Storage, uint Type)> Pointers = new();
    public readonly Dictionary<uint, (uint PointerType, uint Storage)> Variables = new();
    public readonly Dictionary<uint, string> ScalarTypes = new();
    public readonly Dictionary<uint, uint> SpecConstantTypes = new();

    public static SpirvReader Parse(byte[] bytes)
    {
        if (bytes.Length < 20 || bytes.Length % 4 != 0) throw new ArgumentException("not a SPIR-V module");
        var words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        if (words[0] != Magic) throw new ArgumentException("bad SPIR-V magic");

        var reader = new SpirvReader();
        for (int at = 5; at < words.Length;)
        {
            ushort opcode = (ushort)(words[at] & 0xFFFF);
            int count = (int)(words[at] >> 16);
            if (count == 0) throw new ArgumentException("zero-length instruction at word " + at);
            ReadOnlySpan<uint> operands = new ReadOnlySpan<uint>(words, at + 1, count - 1);
            reader.Take(opcode, operands);
            at += count;
        }
        return reader;
    }

    private void Take(ushort opcode, ReadOnlySpan<uint> o)
    {
        switch (opcode)
        {
            case OpName: Names[o[0]] = ReadString(o[1..]); break;
            case OpMemberName: MemberNames[(o[0], o[1])] = ReadString(o[2..]); break;
            case OpTypeInt: ScalarTypes[o[0]] = o[2] == 1 ? "int" : "uint"; break;
            case OpTypeFloat: ScalarTypes[o[0]] = "float"; break;
            case OpTypeArray: ArrayElements[o[0]] = o[1]; break;
            case OpTypeStruct: Structs[o[0]] = o[1..].ToArray(); break;
            case OpTypePointer: Pointers[o[0]] = (o[1], o[2]); break;
            case OpSpecConstant: SpecConstantTypes[o[1]] = o[0]; break;
            case OpVariable: Variables[o[1]] = (o[0], o[2]); break;
            case OpDecorate:
                Get(Decorations, o[0])[o[1]] = o.Length > 2 ? o[2] : 1;
                break;
            case OpMemberDecorate:
                if (!MemberDecorations.TryGetValue((o[0], o[1]), out Dictionary<uint, uint>? member))
                {
                    MemberDecorations[(o[0], o[1])] = member = new Dictionary<uint, uint>();
                }
                member[o[2]] = o.Length > 3 ? o[3] : 1;
                break;
        }
    }

    /// <summary>The block type of the variable at <paramref name="set" />/<paramref name="binding" />, or null.</summary>
    public uint? BlockAt(uint set, uint binding)
    {
        foreach ((uint id, (uint pointer, uint _)) in Variables)
        {
            if (!Decorations.TryGetValue(id, out Dictionary<uint, uint>? d)) continue;
            if (d.TryGetValue(DecorationDescriptorSet, out uint s) && s == set &&
                d.TryGetValue(DecorationBinding, out uint b) && b == binding)
            {
                return Pointers[pointer].Type;
            }
        }
        return null;
    }

    /// <summary>The block types of every variable in <paramref name="storage" />.</summary>
    public List<uint> BlocksIn(uint storage)
    {
        var blocks = new List<uint>();
        foreach ((uint _, (uint pointer, uint variableStorage)) in Variables)
        {
            if (variableStorage == storage) blocks.Add(Pointers[pointer].Type);
        }
        return blocks;
    }

    public int MemberCount(uint structType) => Structs[structType].Length;

    public uint MemberOffset(uint structType, int member) =>
        MemberDecorations[(structType, (uint)member)][DecorationOffset];

    public string? MemberName(uint structType, int member) =>
        MemberNames.TryGetValue((structType, (uint)member), out string? name) ? name : null;

    public uint? ArrayStride(uint structType, int member)
    {
        uint type = Structs[structType][member];
        if (!ArrayElements.ContainsKey(type)) return null;
        return Decorations.TryGetValue(type, out Dictionary<uint, uint>? d) &&
               d.TryGetValue(DecorationArrayStride, out uint stride) ? stride : null;
    }

    /// <summary>Specialization constant id to its scalar type name.</summary>
    public Dictionary<uint, string> SpecIds()
    {
        var ids = new Dictionary<uint, string>();
        foreach ((uint id, Dictionary<uint, uint> d) in Decorations)
        {
            if (d.TryGetValue(DecorationSpecId, out uint specId) && SpecConstantTypes.TryGetValue(id, out uint type))
            {
                ids[specId] = ScalarTypes[type];
            }
        }
        return ids;
    }

    private static Dictionary<uint, uint> Get(Dictionary<uint, Dictionary<uint, uint>> map, uint id)
    {
        if (!map.TryGetValue(id, out Dictionary<uint, uint>? value)) map[id] = value = new Dictionary<uint, uint>();
        return value;
    }

    private static string ReadString(ReadOnlySpan<uint> words)
    {
        var bytes = new List<byte>();
        foreach (uint word in words)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                byte b = (byte)(word >> shift);
                if (b == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(b);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
