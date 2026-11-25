using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cairo;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Optimum.Patcher;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Xunit;

namespace Optimum.Tests;

// Issue #730: GuiElementItemSlotGridBase.ComposeSlotOverlays must keep the vanilla four-ret layout
// ([ldc.i4.0, ldc.i4.1, ldc.i4.1, ldc.i4.1]) so Harmony transpilers such as Toolsmith's locate the
// second ret (ldc.i4.1) and the drawItemDamage branch without throwing ArgumentOutOfRangeException.
public sealed class GuiSlotOverlaysCompatTests
{
    private const string GridPatchPath =
        "patches/VintagestoryApi/Client/UI/Elements/Impl/Interactive/Inventory/GuiElementItemSlotGridBase.cs.patch";

    [Fact]
    public void SourcePatch_CombinesLeadingFalseGuards_IntoSingleReturn()
    {
        string patch = PatchReader.ReadPatch(GridPatchPath);
        Assert.Contains(
            "if ((uint)slotIndex >= (uint)slotQuantityTextures.Length || !renderedSlots.ContainsKey(slotId)) return false;",
            patch);
        Assert.DoesNotContain(
            "if ((uint)slotIndex >= (uint)slotQuantityTextures.Length) return false;",
            patch);
    }

    [Fact]
    public void CompiledAndPatchedComposeSlotOverlays_KeepVanillaFourRetLayout_AndToolsmithAnchors()
    {
        // 1. Source-compiled VintagestoryAPI.dll used by Optimum builds/tests.
        string compiledApiPath = typeof(GuiElementItemSlotGridBase).Assembly.Location;
        using (var compiled = AssemblyDefinition.ReadAssembly(compiledApiPath))
        {
            AssertComposeSlotOverlaysLayoutAndAnchors(compiled.MainModule, "compiled VintagestoryAPI.dll");
        }

        // 2. Self-contained vanilla-shaped Cecil fixture patched via ApiPatcher.PatchGuiSlotGridPerformance
        //    so CI runners without local .vanilla/ archives still exercise the exact IL rewriter.
        string contractsPath = FindFirstExisting(
            "bin/Release/net10.0/Optimum.Api.Contracts.dll",
            "optimum-api-contracts/bin/Release/net10.0/Optimum.Api.Contracts.dll")
            ?? typeof(Vintagestory.API.Config.OptimumApiBridge).Assembly.Location;
        using var contracts = AssemblyDefinition.ReadAssembly(contractsPath);
        using (var synthetic = CreateSyntheticVanillaSlotGridModule())
        {
            Assert.Equal(4, ApiPatcher.PatchGuiSlotGridPerformance(synthetic.MainModule, contracts.MainModule));
            Assert.Empty(IlStackVerifier.VerifyModule(synthetic.MainModule));
            AssertComposeSlotOverlaysLayoutAndAnchors(synthetic.MainModule, "synthetic vanilla + ApiPatcher");
        }

        // 3. Real vanilla VintagestoryAPI.dll and shipped VintagestoryAPI-patched.dll when available locally.
        string? vanillaApiPath = FindFirstExisting(
            ".vanilla/linux-x64/vintagestory/VintagestoryAPI.dll",
            ".vanilla/linux-x64/runtime-donors/VintagestoryAPI.dll",
            ".vanilla/win-x64/runtime-donors/VintagestoryAPI.dll",
            ".vanilla/win-x64/vintagestory/VintagestoryAPI.dll");
        if (vanillaApiPath != null)
        {
            using var vanilla = AssemblyDefinition.ReadAssembly(vanillaApiPath);
            Assert.Equal(4, ApiPatcher.PatchGuiSlotGridPerformance(vanilla.MainModule, contracts.MainModule));
            Assert.Empty(IlStackVerifier.VerifyModule(vanilla.MainModule));
            AssertComposeSlotOverlaysLayoutAndAnchors(vanilla.MainModule, "vanilla VintagestoryAPI.dll + ApiPatcher");
        }

        string? shippedPatchedPath = FindFirstExisting("bin/Release/net10.0/VintagestoryAPI-patched.dll");
        if (shippedPatchedPath != null)
        {
            using var shipped = AssemblyDefinition.ReadAssembly(shippedPatchedPath);
            AssertComposeSlotOverlaysLayoutAndAnchors(shipped.MainModule, "VintagestoryAPI-patched.dll");
        }
    }

    private sealed class TestSlotGrid : GuiElementItemSlotGridBase
    {
        private TestSlotGrid() : base(null, null, null, 0, null) { }
    }

    [Fact]
    public void ComposeSlotOverlays_RuntimeBoundsAndEmptyOverlaySkip_WorkWithoutOutOfBounds()
    {
        MethodInfo composeMethod = typeof(GuiElementItemSlotGridBase).GetMethod(
            "ComposeSlotOverlays",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(composeMethod);

        var grid = (GuiElementItemSlotGridBase)RuntimeHelpers.GetUninitializedObject(typeof(TestSlotGrid));
        var slot0 = new ItemSlot(null);
        var slot1 = new ItemSlot(null);
        slot1.Itemstack = new ItemStack(new Item(1));

        var available = new Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot> { [0] = slot0, [1] = slot1 };
        var rendered = new Vintagestory.API.Datastructures.OrderedDictionary<int, ItemSlot> { [0] = slot0, [1] = slot1 };
        var emptyTex0 = (LoadedTexture)RuntimeHelpers.GetUninitializedObject(typeof(LoadedTexture));
        var emptyTex1 = (LoadedTexture)RuntimeHelpers.GetUninitializedObject(typeof(LoadedTexture));

        grid.availableSlots = available;
        grid.renderedSlots = rendered;
        SetNonPublicField(grid, "slotQuantityTextures", new[] { emptyTex0, emptyTex1 });
        grid.SlotBounds = new[] { ElementBounds.Fixed(0, 0, 48, 48), ElementBounds.Fixed(52, 0, 48, 48) };

        // Out-of-bounds negative and upper-bound indices must return false without throwing.
        Assert.False((bool)composeMethod.Invoke(grid, new object[] { slot0, 0, -1 })!);
        Assert.False((bool)composeMethod.Invoke(grid, new object[] { slot0, 0, 2 })!);

        // Filtering slot 0 out of renderedSlots while leaving it in availableSlots must return false.
        rendered.Remove(0);
        Assert.False((bool)composeMethod.Invoke(grid, new object[] { slot0, 0, 0 })!);

        // Empty texture array must return false cleanly.
        SetNonPublicField(grid, "slotQuantityTextures", Array.Empty<LoadedTexture>());
        Assert.False((bool)composeMethod.Invoke(grid, new object[] { slot0, 0, 0 })!);

        // Restore rendered slots and textures: null itemstack returns true; non-damageable item with TextureId == 0
        // skips Dispose and preserves the existing LoadedTexture instance.
        SetNonPublicField(grid, "slotQuantityTextures", new[] { emptyTex0, emptyTex1 });
        rendered.Insert(0, 0, slot0);
        Assert.True((bool)composeMethod.Invoke(grid, new object[] { slot0, 0, 0 })!);
        Assert.True((bool)composeMethod.Invoke(grid, new object[] { slot1, 1, 1 })!);
        Assert.Same(emptyTex1, GetSlotQuantityTextures(grid)[1]);
    }

    private static void AssertComposeSlotOverlaysLayoutAndAnchors(ModuleDefinition module, string sourceLabel)
    {
        var grid = module.GetType("Vintagestory.API.Client.GuiElementItemSlotGridBase");
        Assert.NotNull(grid);
        var method = grid!.Methods.Single(m => !m.IsStatic && m.Name == "ComposeSlotOverlays" && m.Parameters.Count == 3);
        var instructions = method.Body.Instructions;

        // Verify opening bounds check branches to the existing opening false return (ldc.i4.0; ret)
        // rather than emitting an extra ret instruction.
        Assert.True(instructions.Count >= 6, $"{sourceLabel}: method body too short");
        Assert.Equal(OpCodes.Ldarg_3, instructions[0].OpCode);
        Assert.Equal(OpCodes.Ldarg_0, instructions[1].OpCode);
        Assert.Equal(OpCodes.Ldfld, instructions[2].OpCode);
        Assert.Equal("slotQuantityTextures", ((FieldReference)instructions[2].Operand).Name);
        Assert.Equal(OpCodes.Ldlen, instructions[3].OpCode);
        Assert.Equal(OpCodes.Conv_I4, instructions[4].OpCode);
        Assert.True(
            instructions[5].OpCode == OpCodes.Bge_Un || instructions[5].OpCode == OpCodes.Bge_Un_S,
            $"{sourceLabel}: expected unsigned Bge_Un bounds branch at IL[5], found {instructions[5].OpCode}");
        var guardTarget = (Instruction)instructions[5].Operand;
        Assert.Equal(OpCodes.Ldc_I4_0, guardTarget.OpCode);
        Assert.Equal(OpCodes.Ret, guardTarget.Next.OpCode);

        // Exact vanilla ret count and return-value constants: [false, true, true, true].
        var rets = instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        Assert.Equal(4, rets.Count);
        Assert.Equal(
            new[] { OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_1 },
            rets.Select(r => r.Previous.OpCode).ToArray());

        // Toolsmith 1.2.19 transpiler anchor logic: finds 2nd ret with Ldc_I4_1 before it, then Brtrue_S,
        // three CollectibleObject durability callvirts, and two ShadePath(..., 2.0) calls.
        int retCount1219 = 0;
        int shadePathCount1219 = 0;
        int insertIndex1219 = -1;
        int secondRetFlag1219 = -1;
        int renderDamageBranch1219 = -1;
        int damageColorIdx1219 = -1;
        int maxDuraIdx1219 = -1;
        int remDuraIdx1219 = -1;

        for (int i = 0; i < instructions.Count; i++)
        {
            if (retCount1219 < 2 && instructions[i].OpCode == OpCodes.Ret)
            {
                retCount1219++;
                if (retCount1219 == 2 && instructions[i - 1].OpCode == OpCodes.Ldc_I4_1)
                {
                    secondRetFlag1219 = i;
                }
                continue;
            }
            if (retCount1219 == 2 && secondRetFlag1219 > 1 &&
                (instructions[i].OpCode == OpCodes.Brtrue_S || instructions[i].OpCode == OpCodes.Brtrue))
            {
                renderDamageBranch1219 = i;
                secondRetFlag1219 = 1;
                continue;
            }
            if (retCount1219 == 2 && shadePathCount1219 < 2 && instructions[i].OpCode == OpCodes.Callvirt &&
                instructions[i].Operand is MethodReference virtRef)
            {
                if (virtRef.Name == "GetItemDamageColor") { damageColorIdx1219 = i; continue; }
                if (virtRef.Name == "GetMaxDurability") { maxDuraIdx1219 = i; continue; }
                if (virtRef.Name == "GetRemainingDurability") { remDuraIdx1219 = i; continue; }
            }
            if (retCount1219 == 2 && shadePathCount1219 < 2 &&
                instructions[i].OpCode == OpCodes.Call &&
                instructions[i - 1].OpCode == OpCodes.Ldc_R8 &&
                instructions[i - 1].Operand is double d && d == 2.0 &&
                instructions[i - 2].OpCode == OpCodes.Ldloc_2)
            {
                shadePathCount1219++;
            }
            else if (shadePathCount1219 == 2)
            {
                insertIndex1219 = i;
                break;
            }
        }

        Assert.True(secondRetFlag1219 >= 0, $"{sourceLabel}: Toolsmith 1.2.19 2nd ret (ldc.i4.1) anchor failed");
        Assert.True(renderDamageBranch1219 >= 0, $"{sourceLabel}: Toolsmith 1.2.19 drawItemDamage branch anchor failed");
        Assert.NotNull(instructions[renderDamageBranch1219].Operand);
        Assert.True(damageColorIdx1219 >= 0 && maxDuraIdx1219 >= 0 && remDuraIdx1219 >= 0, $"{sourceLabel}: Toolsmith 1.2.19 durability callvirt anchor failed");
        Assert.True(insertIndex1219 >= 0, $"{sourceLabel}: Toolsmith 1.2.19 ShadePath anchor failed");

        // Toolsmith 1.2.20 transpiler anchor logic: locates first Stloc_0 with i+2 in bounds.
        int renderDamageBranch1220 = -1;
        int shadePathCount1220 = 0;
        int insertIndex1220 = -1;
        int damageColorIdx1220 = -1;
        int maxDuraIdx1220 = -1;
        int remDuraIdx1220 = -1;

        for (int i = 0; i < instructions.Count; i++)
        {
            if (renderDamageBranch1220 < 0 && instructions[i].OpCode == OpCodes.Stloc_0 && i + 2 < instructions.Count)
            {
                renderDamageBranch1220 = i + 2;
                continue;
            }
            if (shadePathCount1220 < 2 && instructions[i].OpCode == OpCodes.Callvirt &&
                instructions[i].Operand is MethodReference virtRef)
            {
                if (virtRef.Name == "GetItemDamageColor") { damageColorIdx1220 = i; continue; }
                if (virtRef.Name == "GetMaxDurability") { maxDuraIdx1220 = i; continue; }
                if (virtRef.Name == "GetRemainingDurability") { remDuraIdx1220 = i; continue; }
            }
            if (shadePathCount1220 < 2 &&
                instructions[i].OpCode == OpCodes.Call &&
                instructions[i - 1].OpCode == OpCodes.Ldc_R8 &&
                instructions[i - 1].Operand is double d && d == 2.0 &&
                instructions[i - 2].OpCode == OpCodes.Ldloc_2)
            {
                shadePathCount1220++;
            }
            else if (shadePathCount1220 == 2)
            {
                insertIndex1220 = i;
                break;
            }
        }

        Assert.True(renderDamageBranch1220 >= 0, $"{sourceLabel}: Toolsmith 1.2.20 stloc.0 anchor failed");
        Assert.True(
            instructions[renderDamageBranch1220].OpCode == OpCodes.Brtrue_S || instructions[renderDamageBranch1220].OpCode == OpCodes.Brtrue,
            $"{sourceLabel}: Toolsmith 1.2.20 expected brtrue at stloc.0+2, found {instructions[renderDamageBranch1220].OpCode}");
        Assert.NotNull(instructions[renderDamageBranch1220].Operand);
        Assert.True(damageColorIdx1220 >= 0 && maxDuraIdx1220 >= 0 && remDuraIdx1220 >= 0, $"{sourceLabel}: Toolsmith 1.2.20 durability callvirt anchor failed");
        Assert.True(insertIndex1220 >= 0, $"{sourceLabel}: Toolsmith 1.2.20 ShadePath anchor failed");
    }

    private static AssemblyDefinition CreateSyntheticVanillaSlotGridModule()
    {
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("VintagestoryAPI", new Version(1, 0, 0, 0)),
            "VintagestoryAPI",
            ModuleKind.Dll);
        var module = asm.MainModule;

        var elementBounds = new TypeDefinition("Vintagestory.API.Client", "ElementBounds", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(elementBounds);

        var loadedTexture = new TypeDefinition("Vintagestory.API.Client", "LoadedTexture", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
        var texIdField = new FieldDefinition("TextureId", Mono.Cecil.FieldAttributes.Public, module.TypeSystem.Int32);
        loadedTexture.Fields.Add(texIdField);
        var apiRefType = new TypeDefinition("Vintagestory.API.Client", "ICoreClientAPI", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Interface | Mono.Cecil.TypeAttributes.Abstract, null);
        module.Types.Add(apiRefType);
        var texCtor = new MethodDefinition(".ctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        texCtor.Parameters.Add(new ParameterDefinition(apiRefType));
        texCtor.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        loadedTexture.Methods.Add(texCtor);
        var texDispose = new MethodDefinition("Dispose", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.Virtual, module.TypeSystem.Void);
        texDispose.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        loadedTexture.Methods.Add(texDispose);
        module.Types.Add(loadedTexture);

        var orderedDictType = module.ImportReference(typeof(Vintagestory.API.Datastructures.OrderedDictionary<,>));
        var itemSlotType = module.ImportReference(typeof(ItemSlot));
        var slotsDictType = new GenericInstanceType(orderedDictType);
        slotsDictType.GenericArguments.Add(module.TypeSystem.Int32);
        slotsDictType.GenericArguments.Add(itemSlotType);

        var guiElement = new TypeDefinition("Vintagestory.API.Client", "GuiElement", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
        var apiField = new FieldDefinition("api", Mono.Cecil.FieldAttributes.Public, apiRefType);
        var boundsField = new FieldDefinition("Bounds", Mono.Cecil.FieldAttributes.Public, elementBounds);
        guiElement.Fields.Add(apiField);
        guiElement.Fields.Add(boundsField);
        module.Types.Add(guiElement);

        var grid = new TypeDefinition("Vintagestory.API.Client", "GuiElementItemSlotGridBase", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, guiElement);
        var availableField = new FieldDefinition("availableSlots", Mono.Cecil.FieldAttributes.Public, slotsDictType);
        var renderedField = new FieldDefinition("renderedSlots", Mono.Cecil.FieldAttributes.Public, slotsDictType);
        var slotBoundsField = new FieldDefinition("SlotBounds", Mono.Cecil.FieldAttributes.Public, new ArrayType(elementBounds));
        var slotQtyField = new FieldDefinition("slotQuantityTextures", Mono.Cecil.FieldAttributes.Private, new ArrayType(loadedTexture));
        grid.Fields.Add(availableField);
        grid.Fields.Add(renderedField);
        grid.Fields.Add(slotBoundsField);
        grid.Fields.Add(slotQtyField);
        module.Types.Add(grid);

        // RenderInteractiveElements(float)
        var render = new MethodDefinition("RenderInteractiveElements", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Virtual | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
        render.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        render.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        grid.Methods.Add(render);

        // ComposeInteractiveElements()
        var composeInteractive = new MethodDefinition("ComposeInteractiveElements", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
        composeInteractive.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        grid.Methods.Add(composeInteractive);

        // FilterItemsBySearchText(string, Dictionary<int, string>, Dictionary<int, string>)
        var dictIntStrType = module.ImportReference(typeof(Dictionary<int, string>));
        var filter = new MethodDefinition("FilterItemsBySearchText", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
        filter.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        filter.Parameters.Add(new ParameterDefinition(dictIntStrType));
        filter.Parameters.Add(new ParameterDefinition(dictIntStrType));
        var filIl = filter.Body.GetILProcessor();
        filIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        filIl.Append(Instruction.Create(OpCodes.Call, composeInteractive));
        filIl.Append(Instruction.Create(OpCodes.Ret));
        grid.Methods.Add(filter);

        // PostRenderInteractiveElements(float)
        var indexOfKeyMethod = new MethodReference("IndexOfKey", module.TypeSystem.Int32, orderedDictType) { HasThis = true };
        indexOfKeyMethod.Parameters.Add(new ParameterDefinition(orderedDictType.GenericParameters[0]));
        var indexOfKeyInst = new MethodReference("IndexOfKey", module.TypeSystem.Int32, slotsDictType) { HasThis = true };
        indexOfKeyInst.Parameters.Add(new ParameterDefinition(orderedDictType.GenericParameters[0]));

        var containsKeyMethod = new MethodReference("ContainsKey", module.TypeSystem.Boolean, slotsDictType) { HasThis = true };
        containsKeyMethod.Parameters.Add(new ParameterDefinition(orderedDictType.GenericParameters[0]));

        var postRender = new MethodDefinition("PostRenderInteractiveElements", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Virtual | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
        postRender.Parameters.Add(new ParameterDefinition(module.TypeSystem.Single));
        var prIl = postRender.Body.GetILProcessor();
        prIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        prIl.Append(Instruction.Create(OpCodes.Ldfld, availableField));
        prIl.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        prIl.Append(Instruction.Create(OpCodes.Callvirt, indexOfKeyInst));
        prIl.Append(Instruction.Create(OpCodes.Pop));
        prIl.Append(Instruction.Create(OpCodes.Ret));
        grid.Methods.Add(postRender);

        // Vanilla-equivalent ComposeSlotOverlays(ItemSlot, int, int) with exact 4 rets and Toolsmith anchors.
        var getItemstack = module.ImportReference(typeof(ItemSlot).GetProperty("Itemstack")!.GetGetMethod()!);
        var getCollectible = module.ImportReference(typeof(ItemStack).GetProperty("Collectible")!.GetGetMethod()!);
        var shouldDisplayDamage = module.ImportReference(typeof(CollectibleObject).GetMethod("ShouldDisplayItemDamage", [typeof(ItemStack)])!);
        var getDamageColor = module.ImportReference(typeof(CollectibleObject).GetMethod("GetItemDamageColor", [typeof(ItemStack)])!);
        var getMaxDura = module.ImportReference(typeof(CollectibleObject).GetMethod("GetMaxDurability", [typeof(ItemStack)])!);
        var getRemDura = module.ImportReference(typeof(CollectibleObject).GetMethod("GetRemainingDurability", [typeof(ItemStack)])!);

        var shadePath = new MethodDefinition("ShadePath", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Void);
        shadePath.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(Context))));
        shadePath.Parameters.Add(new ParameterDefinition(module.TypeSystem.Double));
        shadePath.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
        guiElement.Methods.Add(shadePath);

        var composeOverlays = new MethodDefinition("ComposeSlotOverlays", Mono.Cecil.MethodAttributes.Private | Mono.Cecil.MethodAttributes.HideBySig, module.TypeSystem.Boolean);
        composeOverlays.Parameters.Add(new ParameterDefinition("slot", Mono.Cecil.ParameterAttributes.None, itemSlotType));
        composeOverlays.Parameters.Add(new ParameterDefinition("slotId", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
        composeOverlays.Parameters.Add(new ParameterDefinition("slotIndex", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
        composeOverlays.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Boolean));
        composeOverlays.Body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(ImageSurface))));
        composeOverlays.Body.Variables.Add(new VariableDefinition(module.ImportReference(typeof(Context))));
        composeOverlays.Body.InitLocals = true;

        var ovIl = composeOverlays.Body.GetILProcessor();
        var checkItemstack = Instruction.Create(OpCodes.Ldarg_1);
        var checkDamage = Instruction.Create(OpCodes.Ldarg_1);
        var drawBranch = Instruction.Create(OpCodes.Ldarg_1);
        var endRetLoad = Instruction.Create(OpCodes.Ldc_I4_1);

        // Ret #1: !availableSlots.ContainsKey(slotId) -> ldc.i4.0; ret
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        ovIl.Append(Instruction.Create(OpCodes.Ldfld, availableField));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_2));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, containsKeyInst(containsKeyMethod)));
        ovIl.Append(Instruction.Create(OpCodes.Brtrue_S, checkItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        ovIl.Append(Instruction.Create(OpCodes.Ret));

        // Ret #2: slot.Itemstack == null -> ldc.i4.1; ret
        ovIl.Append(checkItemstack);
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Brtrue_S, checkDamage));
        ovIl.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        ovIl.Append(Instruction.Create(OpCodes.Ret));

        // Ret #3: !drawItemDamage -> Dispose, new LoadedTexture, stelem.ref, ldc.i4.1, ret
        ovIl.Append(checkDamage);
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getCollectible));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, shouldDisplayDamage));
        ovIl.Append(Instruction.Create(OpCodes.Stloc_0));
        ovIl.Append(Instruction.Create(OpCodes.Ldloc_0));
        ovIl.Append(Instruction.Create(OpCodes.Brtrue_S, drawBranch));

        ovIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        ovIl.Append(Instruction.Create(OpCodes.Ldfld, slotQtyField));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_3));
        ovIl.Append(Instruction.Create(OpCodes.Ldelem_Ref));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, texDispose));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        ovIl.Append(Instruction.Create(OpCodes.Ldfld, slotQtyField));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_3));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        ovIl.Append(Instruction.Create(OpCodes.Ldfld, apiField));
        ovIl.Append(Instruction.Create(OpCodes.Newobj, texCtor));
        ovIl.Append(Instruction.Create(OpCodes.Stelem_Ref));
        ovIl.Append(Instruction.Create(OpCodes.Ldc_I4_1));
        ovIl.Append(Instruction.Create(OpCodes.Ret));

        // Damage bar drawing section containing Toolsmith anchors + Ret #4
        ovIl.Append(drawBranch);
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getCollectible));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getDamageColor));
        ovIl.Append(Instruction.Create(OpCodes.Pop));

        ovIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        ovIl.Append(Instruction.Create(OpCodes.Ldloc_2));
        ovIl.Append(Instruction.Create(OpCodes.Ldc_R8, 2.0));
        ovIl.Append(Instruction.Create(OpCodes.Call, shadePath));

        ovIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getCollectible));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getMaxDura));
        ovIl.Append(Instruction.Create(OpCodes.Pop));

        ovIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getCollectible));
        ovIl.Append(Instruction.Create(OpCodes.Ldarg_1));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getItemstack));
        ovIl.Append(Instruction.Create(OpCodes.Callvirt, getRemDura));
        ovIl.Append(Instruction.Create(OpCodes.Pop));

        ovIl.Append(Instruction.Create(OpCodes.Ldarg_0));
        ovIl.Append(Instruction.Create(OpCodes.Ldloc_2));
        ovIl.Append(Instruction.Create(OpCodes.Ldc_R8, 2.0));
        ovIl.Append(Instruction.Create(OpCodes.Call, shadePath));

        ovIl.Append(endRetLoad);
        ovIl.Append(Instruction.Create(OpCodes.Ret));
        grid.Methods.Add(composeOverlays);

        return asm;

        static MethodReference containsKeyInst(MethodReference r) => r;
    }

    private static string? FindFirstExisting(params string[] relativePaths)
    {
        foreach (string relative in relativePaths)
        {
            try
            {
                string path = PatchReader.FindRepositoryFile(relative);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (FileNotFoundException)
            {
            }
        }
        return null;
    }

    private static void SetNonPublicField(object target, string name, object value)
    {
        FieldInfo? field = typeof(GuiElementItemSlotGridBase).GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static LoadedTexture[] GetSlotQuantityTextures(GuiElementItemSlotGridBase grid)
    {
        FieldInfo? field = typeof(GuiElementItemSlotGridBase).GetField(
            "slotQuantityTextures",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (LoadedTexture[])field!.GetValue(grid)!;
    }
}
