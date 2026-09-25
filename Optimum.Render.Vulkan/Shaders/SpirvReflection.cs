using System;
using System.Collections.Generic;
using System.Text;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>What a descriptor binding holds, as the SPIR-V declares it.</summary>
internal enum SpirvDescriptorKind
{
    CombinedImageSampler,
    SampledImage,
    StorageImage,
    Sampler,
    UniformBuffer,
    StorageBuffer,
}

/// <summary>A member of a uniform, push-constant or storage block.</summary>
internal sealed class SpirvBlockMember
{
    public string Name = "";
    /// <summary>The GLSL spelling of the element type (<c>mat4</c>, <c>vec3</c>, a struct's type name).</summary>
    public string GlslType = "";
    public int Offset;
    /// <summary>Bytes the member occupies: element size, or length times the array stride.</summary>
    public int Size;
    /// <summary>0 when not an array, -1 for a runtime-sized array.</summary>
    public int ArrayLength;
}

/// <summary>A struct used as a block, with explicit member offsets.</summary>
internal sealed class SpirvBlock
{
    public string TypeName = "";
    public string InstanceName = "";
    /// <summary>End of the last member, which is what a scalar-layout block needs in bytes.</summary>
    public int Size;
    public List<SpirvBlockMember> Members = new();
}

internal sealed class SpirvDescriptorBinding
{
    public uint VariableId;
    public string Name = "";
    public int Set;
    public int Binding;
    public SpirvDescriptorKind Kind;
    /// <summary>For images and samplers the GLSL type (<c>sampler2DArrayShadow</c>); for blocks the block type name.</summary>
    public string GlslType = "";
    /// <summary>0 when not an array.</summary>
    public int ArrayLength;
    public bool RuntimeArray;
    /// <summary>The block layout for uniform and storage buffers, null otherwise.</summary>
    public SpirvBlock? Block;
}

internal sealed class SpirvInterfaceVariable
{
    public uint VariableId;
    public string Name = "";
    public int Location;
    public string GlslType = "";
    /// <summary>0 when not an array.</summary>
    public int ArrayLength;
}

internal sealed class SpirvSpecConstant
{
    public int SpecId;
    public string Name = "";
    /// <summary><c>bool</c>, <c>int</c>, <c>uint</c>, <c>float</c> or <c>double</c>.</summary>
    public string GlslType = "";
    /// <summary>The default as a number; a bool is 0 or 1.</summary>
    public double DefaultValue;
}

/// <summary>Everything the native shader manifest needs from one SPIR-V module.</summary>
internal sealed class SpirvModuleReflection
{
    public string EntryPoint = "";
    /// <summary>The SPIR-V execution model: 0 vertex, 3 geometry, 4 fragment, 5 compute.</summary>
    public int ExecutionModel;
    public List<SpirvInterfaceVariable> Inputs = new();
    public List<SpirvInterfaceVariable> Outputs = new();
    public List<SpirvDescriptorBinding> Bindings = new();
    public SpirvBlock? PushConstants;
    public List<SpirvSpecConstant> SpecConstants = new();
    /// <summary>Descriptor, push and interface variables some function body actually uses.</summary>
    public HashSet<uint> UsedVariables = new();
    /// <summary>Locations of the outputs a function body stores to (array outputs contribute every element).</summary>
    public SortedSet<int> WrittenOutputLocations = new();
    /// <summary>
    /// For every push-constant member whose value is used as the first index into an arrayed
    /// descriptor: the (set, binding) pairs it indexes. This is how a sampler slot is tied to the
    /// bindless array it selects from, independent of names (an optimised module has none).
    /// </summary>
    public Dictionary<int, SortedSet<(int Set, int Binding)>> PushMemberIndexes = new();
}

/// <summary>
/// A small SPIR-V reader for the offline shader compiler and the native program manifest
/// (docs/vulkan.md). It reads entry points, names, decorations and the
/// type graph; it does not validate the module, which shaderc produced moments earlier.
///
/// An optimised module has no <c>OpName</c>s and has dropped whatever nothing uses, so the
/// builder reflects two modules per stage: the unoptimised one for declarations and names,
/// the shipped one for use (<see cref="SpirvModuleReflection.UsedVariables" />, written outputs,
/// slot dataflow).
/// </summary>
internal static class SpirvReflection
{
    public const uint Magic = 0x07230203;

    public const int ExecutionModelVertex = 0;
    public const int ExecutionModelGeometry = 3;
    public const int ExecutionModelFragment = 4;

    // Opcodes.
    private const int OpName = 5;
    private const int OpMemberName = 6;
    private const int OpEntryPoint = 15;
    private const int OpTypeVoid = 19;
    private const int OpTypeBool = 20;
    private const int OpTypeInt = 21;
    private const int OpTypeFloat = 22;
    private const int OpTypeVector = 23;
    private const int OpTypeMatrix = 24;
    private const int OpTypeImage = 25;
    private const int OpTypeSampler = 26;
    private const int OpTypeSampledImage = 27;
    private const int OpTypeArray = 28;
    private const int OpTypeRuntimeArray = 29;
    private const int OpTypeStruct = 30;
    private const int OpTypePointer = 32;
    private const int OpConstantTrue = 41;
    private const int OpConstantFalse = 42;
    private const int OpConstant = 43;
    private const int OpSpecConstantTrue = 48;
    private const int OpSpecConstantFalse = 49;
    private const int OpSpecConstant = 50;
    private const int OpFunction = 54;
    private const int OpFunctionCall = 57;
    private const int OpVariable = 59;
    private const int OpImageTexelPointer = 60;
    private const int OpLoad = 61;
    private const int OpStore = 62;
    private const int OpCopyMemory = 63;
    private const int OpAccessChain = 65;
    private const int OpInBoundsAccessChain = 66;
    private const int OpPtrAccessChain = 67;
    private const int OpArrayLength = 68;
    private const int OpCompositeExtract = 81;
    private const int OpCopyObject = 83;
    private const int OpUConvert = 113;
    private const int OpSConvert = 114;
    private const int OpBitcast = 124;

    // Decorations.
    private const int DecorationSpecId = 1;
    private const int DecorationBlock = 2;
    private const int DecorationBufferBlock = 3;
    private const int DecorationArrayStride = 6;
    private const int DecorationMatrixStride = 7;
    private const int DecorationBuiltIn = 11;
    private const int DecorationLocation = 30;
    private const int DecorationBinding = 33;
    private const int DecorationDescriptorSet = 34;
    private const int DecorationOffset = 35;

    // Storage classes.
    private const int StorageUniformConstant = 0;
    private const int StorageInput = 1;
    private const int StorageUniform = 2;
    private const int StorageOutput = 3;
    private const int StoragePushConstant = 9;
    private const int StorageStorageBuffer = 12;

    private sealed class TypeInfo
    {
        public int Op;
        public uint[] Operands = Array.Empty<uint>();
    }

    private sealed class Module
    {
        public readonly Dictionary<uint, string> Names = new();
        public readonly Dictionary<(uint, int), string> MemberNames = new();
        public readonly Dictionary<uint, List<(int Decoration, uint[] Literals)>> Decorations = new();
        public readonly Dictionary<(uint, int), List<(int Decoration, uint[] Literals)>> MemberDecorations = new();
        public readonly Dictionary<uint, TypeInfo> Types = new();
        public readonly Dictionary<uint, (uint Type, uint[] Literals)> Constants = new();
        public readonly List<(uint Id, uint Type, int Op, uint[] Literals)> SpecConstants = new();
        public readonly Dictionary<uint, (uint PointerType, int StorageClass)> Variables = new();
        public readonly List<(int Op, uint[] Operands)> Body = new();
        public string EntryName = "";
        public int ExecutionModel = -1;
        public uint[] Interface = Array.Empty<uint>();
    }

    public static SpirvModuleReflection Reflect(byte[] spirv)
    {
        if (spirv == null || spirv.Length < 20 || spirv.Length % 4 != 0)
        {
            throw new FormatException("not a SPIR-V module: length " + (spirv?.Length ?? 0));
        }

        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        if (words[0] != Magic)
        {
            throw new FormatException("not a SPIR-V module: magic 0x" + words[0].ToString("x8"));
        }

        Module module = Parse(words);
        return Build(module);
    }

    private static Module Parse(uint[] words)
    {
        var module = new Module();
        bool inFunctions = false;
        int position = 5;
        while (position < words.Length)
        {
            int count = (int)(words[position] >> 16);
            int op = (int)(words[position] & 0xFFFF);
            if (count == 0 || position + count > words.Length)
            {
                throw new FormatException("truncated SPIR-V instruction at word " + position);
            }
            var operands = new uint[count - 1];
            Array.Copy(words, position + 1, operands, 0, count - 1);
            position += count;

            if (op == OpFunction) inFunctions = true;
            if (inFunctions)
            {
                module.Body.Add((op, operands));
                continue;
            }

            switch (op)
            {
                case OpName:
                    module.Names[operands[0]] = ReadString(operands, 1, out _);
                    break;
                case OpMemberName:
                    module.MemberNames[(operands[0], (int)operands[1])] = ReadString(operands, 2, out _);
                    break;
                case OpEntryPoint:
                    if (module.ExecutionModel < 0)
                    {
                        module.ExecutionModel = (int)operands[0];
                        module.EntryName = ReadString(operands, 2, out int next);
                        module.Interface = operands[next..];
                    }
                    break;
                case 71: // OpDecorate
                    Add(module.Decorations, operands[0], ((int)operands[1], operands[2..]));
                    break;
                case 72: // OpMemberDecorate
                    Add(module.MemberDecorations, (operands[0], (int)operands[1]), ((int)operands[2], operands[3..]));
                    break;
                case OpTypeVoid:
                case OpTypeBool:
                case OpTypeInt:
                case OpTypeFloat:
                case OpTypeVector:
                case OpTypeMatrix:
                case OpTypeImage:
                case OpTypeSampler:
                case OpTypeSampledImage:
                case OpTypeArray:
                case OpTypeRuntimeArray:
                case OpTypeStruct:
                    module.Types[operands[0]] = new TypeInfo { Op = op, Operands = operands[1..] };
                    break;
                case OpTypePointer:
                    module.Types[operands[0]] = new TypeInfo { Op = op, Operands = operands[1..] };
                    break;
                case OpConstant:
                    module.Constants[operands[1]] = (operands[0], operands[2..]);
                    break;
                case OpConstantTrue:
                case OpConstantFalse:
                    module.Constants[operands[1]] = (operands[0], new uint[] { op == OpConstantTrue ? 1u : 0u });
                    break;
                case OpSpecConstant:
                case OpSpecConstantTrue:
                case OpSpecConstantFalse:
                    module.SpecConstants.Add((operands[1], operands[0], op, operands[2..]));
                    break;
                case OpVariable:
                    module.Variables[operands[1]] = (operands[0], (int)operands[2]);
                    break;
            }
        }
        return module;
    }

    private static SpirvModuleReflection Build(Module module)
    {
        var result = new SpirvModuleReflection
        {
            EntryPoint = module.EntryName,
            ExecutionModel = module.ExecutionModel,
        };

        uint? pushVariable = null;
        foreach ((uint id, (uint pointerType, int storageClass)) in module.Variables)
        {
            uint pointee = module.Types.TryGetValue(pointerType, out TypeInfo? pointer) && pointer.Op == OpTypePointer
                ? pointer.Operands[1]
                : 0;
            if (pointee == 0) continue;

            switch (storageClass)
            {
                case StorageInput:
                case StorageOutput:
                {
                    if (HasDecoration(module, id, DecorationBuiltIn) || IsBuiltInBlock(module, pointee)) continue;
                    if (!TryDecoration(module, id, DecorationLocation, out uint location)) continue;
                    UnwrapArray(module, pointee, out uint element, out int length, out _);
                    var variable = new SpirvInterfaceVariable
                    {
                        VariableId = id,
                        Name = NameOf(module, id),
                        Location = (int)location,
                        GlslType = GlslTypeName(module, element),
                        ArrayLength = length,
                    };
                    (storageClass == StorageInput ? result.Inputs : result.Outputs).Add(variable);
                    break;
                }
                case StoragePushConstant:
                    pushVariable = id;
                    result.PushConstants = ReadBlock(module, pointee, id);
                    break;
                case StorageUniformConstant:
                case StorageUniform:
                case StorageStorageBuffer:
                {
                    if (!TryDecoration(module, id, DecorationDescriptorSet, out uint set) ||
                        !TryDecoration(module, id, DecorationBinding, out uint binding))
                    {
                        continue;
                    }
                    UnwrapArray(module, pointee, out uint element, out int length, out bool runtime);
                    var descriptor = new SpirvDescriptorBinding
                    {
                        VariableId = id,
                        Name = NameOf(module, id),
                        Set = (int)set,
                        Binding = (int)binding,
                        ArrayLength = length,
                        RuntimeArray = runtime,
                    };
                    TypeInfo elementType = module.Types[element];
                    if (elementType.Op == OpTypeStruct)
                    {
                        bool bufferBlock = HasDecoration(module, element, DecorationBufferBlock);
                        descriptor.Kind = storageClass == StorageStorageBuffer || bufferBlock
                            ? SpirvDescriptorKind.StorageBuffer
                            : SpirvDescriptorKind.UniformBuffer;
                        descriptor.Block = ReadBlock(module, element, id);
                        descriptor.GlslType = descriptor.Block.TypeName;
                    }
                    else
                    {
                        descriptor.Kind = elementType.Op switch
                        {
                            OpTypeSampledImage => SpirvDescriptorKind.CombinedImageSampler,
                            OpTypeSampler => SpirvDescriptorKind.Sampler,
                            OpTypeImage when elementType.Operands[5] == 2 => SpirvDescriptorKind.StorageImage,
                            _ => SpirvDescriptorKind.SampledImage,
                        };
                        descriptor.GlslType = GlslTypeName(module, element);
                    }
                    result.Bindings.Add(descriptor);
                    break;
                }
            }
        }

        result.Inputs.Sort((a, b) => a.Location.CompareTo(b.Location));
        result.Outputs.Sort((a, b) => a.Location.CompareTo(b.Location));
        result.Bindings.Sort((a, b) => a.Set != b.Set ? a.Set.CompareTo(b.Set) : a.Binding.CompareTo(b.Binding));

        foreach ((uint id, uint type, int op, uint[] literals) in module.SpecConstants)
        {
            if (!TryDecoration(module, id, DecorationSpecId, out uint specId)) continue;
            string typeName = GlslTypeName(module, type);
            result.SpecConstants.Add(new SpirvSpecConstant
            {
                SpecId = (int)specId,
                Name = NameOf(module, id),
                GlslType = typeName,
                DefaultValue = op switch
                {
                    OpSpecConstantTrue => 1,
                    OpSpecConstantFalse => 0,
                    _ => DecodeScalar(module, type, literals),
                },
            });
        }
        result.SpecConstants.Sort((a, b) => a.SpecId.CompareTo(b.SpecId));

        AnalyseBodies(module, result, pushVariable);
        return result;
    }

    /// <summary>
    /// One pass over the function bodies: which variables are used, which outputs are stored to,
    /// and which push members index which arrayed descriptors.
    /// </summary>
    private static void AnalyseBodies(Module module, SpirvModuleReflection result, uint? pushVariable)
    {
        // Pointer results rooted at a global variable.
        var roots = new Dictionary<uint, uint>();
        // Pointer results that address one top-level member of the push block.
        var pushMemberPointers = new Dictionary<uint, int>();
        // Values that carry a push member's value unchanged (or through an integer cast).
        var pushMemberValues = new Dictionary<uint, int>();
        // Values that hold the whole push block.
        var pushStructValues = new HashSet<uint>();

        uint Root(uint id) => roots.TryGetValue(id, out uint root) ? root : id;

        void Use(uint pointer)
        {
            uint root = Root(pointer);
            if (module.Variables.ContainsKey(root)) result.UsedVariables.Add(root);
        }

        void Write(uint pointer)
        {
            uint root = Root(pointer);
            if (!module.Variables.TryGetValue(root, out (uint PointerType, int StorageClass) variable) ||
                variable.StorageClass != StorageOutput)
            {
                return;
            }
            if (!TryDecoration(module, root, DecorationLocation, out uint location)) return;
            uint pointee = module.Types[variable.PointerType].Operands[1];
            UnwrapArray(module, pointee, out _, out int length, out _);
            for (int i = 0; i < Math.Max(1, length); i++) result.WrittenOutputLocations.Add((int)location + i);
        }

        foreach ((int op, uint[] operands) in module.Body)
        {
            switch (op)
            {
                case OpAccessChain:
                case OpInBoundsAccessChain:
                case OpPtrAccessChain:
                {
                    uint id = operands[1];
                    uint baseId = operands[2];
                    roots[id] = Root(baseId);
                    Use(baseId);

                    int firstIndex = op == OpPtrAccessChain ? 4 : 3;
                    if (pushVariable.HasValue && baseId == pushVariable.Value && operands.Length == firstIndex + 1 &&
                        TryConstant(module, operands[firstIndex], out long member))
                    {
                        pushMemberPointers[id] = (int)member;
                    }

                    if (operands.Length > firstIndex &&
                        module.Variables.TryGetValue(baseId, out (uint PointerType, int StorageClass) arrayVariable) &&
                        arrayVariable.StorageClass is StorageUniformConstant or StorageUniform or StorageStorageBuffer &&
                        pushMemberValues.TryGetValue(operands[firstIndex], out int slotMember) &&
                        TryDecoration(module, baseId, DecorationDescriptorSet, out uint set) &&
                        TryDecoration(module, baseId, DecorationBinding, out uint binding))
                    {
                        if (!result.PushMemberIndexes.TryGetValue(slotMember, out SortedSet<(int, int)>? indexed))
                        {
                            indexed = new SortedSet<(int, int)>();
                            result.PushMemberIndexes[slotMember] = indexed;
                        }
                        indexed.Add(((int)set, (int)binding));
                    }
                    break;
                }
                case OpLoad:
                {
                    uint id = operands[1];
                    uint pointer = operands[2];
                    Use(pointer);
                    if (pushMemberPointers.TryGetValue(pointer, out int member)) pushMemberValues[id] = member;
                    if (pushVariable.HasValue && pointer == pushVariable.Value) pushStructValues.Add(id);
                    break;
                }
                case OpStore:
                    Use(operands[0]);
                    Write(operands[0]);
                    break;
                case OpCopyMemory:
                    Use(operands[0]);
                    Use(operands[1]);
                    Write(operands[0]);
                    break;
                case OpImageTexelPointer:
                case OpArrayLength:
                    Use(operands[2]);
                    break;
                case OpFunctionCall:
                    for (int i = 3; i < operands.Length; i++) Use(operands[i]);
                    break;
                case OpCompositeExtract:
                    if (pushStructValues.Contains(operands[2]) && operands.Length == 4)
                    {
                        pushMemberValues[operands[1]] = (int)operands[3];
                    }
                    break;
                case OpCopyObject:
                case OpUConvert:
                case OpSConvert:
                case OpBitcast:
                    if (pushMemberValues.TryGetValue(operands[2], out int carried)) pushMemberValues[operands[1]] = carried;
                    if (roots.ContainsKey(operands[2]) || module.Variables.ContainsKey(operands[2]))
                    {
                        roots[operands[1]] = Root(operands[2]);
                    }
                    break;
            }
        }
    }

    private static SpirvBlock ReadBlock(Module module, uint structType, uint variable)
    {
        var block = new SpirvBlock
        {
            TypeName = NameOf(module, structType),
            InstanceName = NameOf(module, variable),
        };
        TypeInfo type = module.Types[structType];
        if (type.Op != OpTypeStruct) return block;

        int end = 0;
        for (int index = 0; index < type.Operands.Length; index++)
        {
            uint memberType = type.Operands[index];
            UnwrapArray(module, memberType, out uint element, out int length, out bool runtime);
            TryMemberDecoration(module, structType, index, DecorationOffset, out uint offset);
            TryMemberDecoration(module, structType, index, DecorationMatrixStride, out uint matrixStride);

            int elementSize = SizeOf(module, element, (int)matrixStride);
            int size;
            if (runtime)
            {
                size = 0;
            }
            else if (length > 0)
            {
                size = TryDecoration(module, memberType, DecorationArrayStride, out uint stride)
                    ? (int)stride * length
                    : elementSize * length;
            }
            else
            {
                size = elementSize;
            }

            block.Members.Add(new SpirvBlockMember
            {
                Name = module.MemberNames.TryGetValue((structType, index), out string? name) ? name : "",
                GlslType = GlslTypeName(module, element),
                Offset = (int)offset,
                Size = size,
                ArrayLength = runtime ? -1 : length,
            });
            end = Math.Max(end, (int)offset + size);
        }
        block.Size = end;
        return block;
    }

    /// <summary>Size of a non-array type under its explicit layout; a matrix uses its member's stride.</summary>
    private static int SizeOf(Module module, uint typeId, int matrixStride)
    {
        TypeInfo type = module.Types[typeId];
        switch (type.Op)
        {
            case OpTypeBool:
                return 4;
            case OpTypeInt:
            case OpTypeFloat:
                return (int)type.Operands[0] / 8;
            case OpTypeVector:
                return SizeOf(module, type.Operands[0], 0) * (int)type.Operands[1];
            case OpTypeMatrix:
            {
                int columns = (int)type.Operands[1];
                int columnSize = SizeOf(module, type.Operands[0], 0);
                return matrixStride > 0 ? (columns - 1) * matrixStride + Math.Max(columnSize, matrixStride) : columns * columnSize;
            }
            case OpTypeArray:
            {
                UnwrapArray(module, typeId, out uint element, out int length, out _);
                return TryDecoration(module, typeId, DecorationArrayStride, out uint stride)
                    ? (int)stride * length
                    : SizeOf(module, element, 0) * length;
            }
            case OpTypeStruct:
            {
                int end = 0;
                for (int index = 0; index < type.Operands.Length; index++)
                {
                    TryMemberDecoration(module, typeId, index, DecorationOffset, out uint offset);
                    TryMemberDecoration(module, typeId, index, DecorationMatrixStride, out uint stride);
                    end = Math.Max(end, (int)offset + SizeOf(module, type.Operands[index], (int)stride));
                }
                return end;
            }
            default:
                return 0;
        }
    }

    private static void UnwrapArray(Module module, uint typeId, out uint element, out int length, out bool runtime)
    {
        element = typeId;
        length = 0;
        runtime = false;
        TypeInfo type = module.Types[typeId];
        if (type.Op == OpTypeRuntimeArray)
        {
            element = type.Operands[0];
            runtime = true;
        }
        else if (type.Op == OpTypeArray)
        {
            element = type.Operands[0];
            length = TryConstant(module, type.Operands[1], out long value) ? (int)value : 0;
        }
    }

    private static bool IsBuiltInBlock(Module module, uint typeId)
    {
        UnwrapArray(module, typeId, out uint element, out _, out _);
        TypeInfo type = module.Types[element];
        if (type.Op != OpTypeStruct) return false;
        for (int index = 0; index < type.Operands.Length; index++)
        {
            if (module.MemberDecorations.TryGetValue((element, index), out var list) &&
                list.Exists(d => d.Decoration == DecorationBuiltIn))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The GLSL spelling of a non-pointer type; arrays spell only their element.</summary>
    private static string GlslTypeName(Module module, uint typeId)
    {
        TypeInfo type = module.Types[typeId];
        switch (type.Op)
        {
            case OpTypeVoid:
                return "void";
            case OpTypeBool:
                return "bool";
            case OpTypeInt:
                return type.Operands[1] != 0 ? "int" : "uint";
            case OpTypeFloat:
                return type.Operands[0] == 64 ? "double" : "float";
            case OpTypeVector:
                return ScalarPrefix(module, type.Operands[0]) + "vec" + type.Operands[1];
            case OpTypeMatrix:
            {
                TypeInfo column = module.Types[type.Operands[0]];
                int rows = (int)column.Operands[1];
                int columns = (int)type.Operands[1];
                string prefix = ScalarPrefix(module, column.Operands[0]) == "d" ? "dmat" : "mat";
                return rows == columns ? prefix + columns : prefix + columns + "x" + rows;
            }
            case OpTypeSampledImage:
                return ImageName(module, type.Operands[0], "sampler");
            case OpTypeImage:
                return ImageName(module, typeId, type.Operands[5] == 2 ? "image" : "texture");
            case OpTypeSampler:
                return "sampler";
            case OpTypeArray:
            case OpTypeRuntimeArray:
                return GlslTypeName(module, type.Operands[0]);
            case OpTypeStruct:
                return NameOf(module, typeId);
            default:
                return "?";
        }
    }

    private static string ScalarPrefix(Module module, uint scalarType)
    {
        TypeInfo scalar = module.Types[scalarType];
        return scalar.Op switch
        {
            OpTypeBool => "b",
            OpTypeInt => scalar.Operands[1] != 0 ? "i" : "u",
            OpTypeFloat when scalar.Operands[0] == 64 => "d",
            _ => "",
        };
    }

    private static string ImageName(Module module, uint imageTypeId, string stem)
    {
        uint[] image = module.Types[imageTypeId].Operands;
        // sampled type, dim, depth, arrayed, multisampled, sampled, format
        string prefix = ScalarPrefix(module, image[0]);
        string dim = image[1] switch
        {
            0 => "1D",
            1 => "2D",
            2 => "3D",
            3 => "Cube",
            4 => "2DRect",
            5 => "Buffer",
            6 => "SubpassData",
            _ => "?",
        };
        return prefix + stem + dim + (image[4] != 0 ? "MS" : "") + (image[3] != 0 ? "Array" : "") +
               (image[2] == 1 ? "Shadow" : "");
    }

    private static double DecodeScalar(Module module, uint typeId, uint[] literals)
    {
        TypeInfo type = module.Types[typeId];
        if (literals.Length == 0) return 0;
        switch (type.Op)
        {
            case OpTypeFloat when type.Operands[0] == 32:
                return BitConverter.Int32BitsToSingle((int)literals[0]);
            case OpTypeFloat:
                return BitConverter.Int64BitsToDouble((long)((ulong)literals[0] | (literals.Length > 1 ? (ulong)literals[1] << 32 : 0)));
            case OpTypeInt when type.Operands[1] != 0:
                return (int)literals[0];
            default:
                return literals[0];
        }
    }

    private static bool TryConstant(Module module, uint id, out long value)
    {
        value = 0;
        if (!module.Constants.TryGetValue(id, out (uint Type, uint[] Literals) constant) || constant.Literals.Length == 0)
        {
            return false;
        }
        TypeInfo type = module.Types[constant.Type];
        value = type.Op == OpTypeInt && type.Operands[1] != 0 ? (int)constant.Literals[0] : constant.Literals[0];
        return true;
    }

    private static string NameOf(Module module, uint id) => module.Names.TryGetValue(id, out string? name) ? name : "";

    private static bool HasDecoration(Module module, uint id, int decoration) =>
        module.Decorations.TryGetValue(id, out var list) && list.Exists(d => d.Decoration == decoration);

    private static bool TryDecoration(Module module, uint id, int decoration, out uint value)
    {
        value = 0;
        if (!module.Decorations.TryGetValue(id, out var list)) return false;
        foreach ((int kind, uint[] literals) in list)
        {
            if (kind != decoration) continue;
            value = literals.Length > 0 ? literals[0] : 0;
            return true;
        }
        return false;
    }

    private static bool TryMemberDecoration(Module module, uint type, int member, int decoration, out uint value)
    {
        value = 0;
        if (!module.MemberDecorations.TryGetValue((type, member), out var list)) return false;
        foreach ((int kind, uint[] literals) in list)
        {
            if (kind != decoration) continue;
            value = literals.Length > 0 ? literals[0] : 0;
            return true;
        }
        return false;
    }

    private static void Add<TKey>(Dictionary<TKey, List<(int, uint[])>> map, TKey key, (int, uint[]) value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<(int, uint[])>? list))
        {
            list = new List<(int, uint[])>();
            map[key] = list;
        }
        list.Add(value);
    }

    private static string ReadString(uint[] operands, int start, out int next)
    {
        var bytes = new List<byte>();
        int index = start;
        for (; index < operands.Length; index++)
        {
            uint word = operands[index];
            bool terminated = false;
            for (int shift = 0; shift < 32; shift += 8)
            {
                byte value = (byte)(word >> shift);
                if (value == 0)
                {
                    terminated = true;
                    break;
                }
                bytes.Add(value);
            }
            if (terminated) break;
        }
        next = Math.Min(index + 1, operands.Length);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
