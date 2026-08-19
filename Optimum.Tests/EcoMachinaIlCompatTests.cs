#if !NO_DONOR
using System;
using System.IO;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Xunit;

namespace Optimum.Tests;

// Eco Machina (vsecomachina) ships a Harmony transpiler for
// ChunkTesselator.CalculateVisibleFaces that injects a neighbor-cull override
// for its render-only tapered trees. The injected IL reads locals by the slot
// numbers of the vanilla 1.22.3 assembly: 4 (i), 6 (j), 9 (k), 13 (num6),
// 14 (block3), 15 (opposite), 16 (flag). Roslyn assigns slots in declaration
// order, so the donor source keeps its declarations in the vanilla order. When
// a slot moves, the injected IL loads a Block where the hook signature expects
// an int, the JIT rejects the patched method, the mod falls back to vanilla
// culling, and every full block under a tapered tree loses its face toward the
// trunk. These tests pin the compiled slot layout and the injection anchor.
public class EcoMachinaIlCompatTests
{
    private static MethodBody CalculateVisibleFacesBody()
    {
        MethodInfo method = typeof(ChunkTesselator).GetMethod(
            "CalculateVisibleFaces",
            new[] { typeof(bool), typeof(int), typeof(int), typeof(int) });
        Assert.NotNull(method);
        MethodBody body = method.GetMethodBody();
        Assert.NotNull(body);
        return body;
    }

    [Theory]
    [InlineData(4, typeof(int))]
    [InlineData(6, typeof(int))]
    [InlineData(9, typeof(int))]
    [InlineData(13, typeof(int))]
    [InlineData(14, typeof(Block))]
    [InlineData(15, typeof(int))]
    [InlineData(16, typeof(bool))]
    public void CalculateVisibleFacesKeepsVanillaLocalSlotLayout(int slot, Type expected)
    {
        MethodBody body = CalculateVisibleFacesBody();
        Assert.True(body.LocalVariables.Count > slot, $"Method has {body.LocalVariables.Count} locals, expected a local at slot {slot}.");
        Assert.Equal(expected, body.LocalVariables[slot].LocalType);
    }

    [Fact]
    public void CalculateVisibleFacesKeepsTheSideOpaqueInjectionAnchor()
    {
        // The transpiler anchors on the first occurrence of:
        //   ldloc.s 14; ldflda Block::SideOpaque; ldloc.s 15;
        //   call SmallBoolArray::get_Item; stloc.s 16
        // Scan the raw IL for that byte sequence and resolve its tokens.
        MethodInfo method = typeof(ChunkTesselator).GetMethod(
            "CalculateVisibleFaces",
            new[] { typeof(bool), typeof(int), typeof(int), typeof(int) });
        byte[] il = method.GetMethodBody().GetILAsByteArray();
        Module module = method.Module;

        bool found = false;
        for (int i = 0; i + 16 <= il.Length; i++)
        {
            if (il[i] != 0x11 || il[i + 1] != 14) continue;      // ldloc.s 14
            if (il[i + 2] != 0x7C) continue;                     // ldflda <field>
            if (il[i + 7] != 0x11 || il[i + 8] != 15) continue;  // ldloc.s 15
            if (il[i + 9] != 0x28) continue;                     // call <method>
            if (il[i + 14] != 0x13 || il[i + 15] != 16) continue; // stloc.s 16

            FieldInfo field = module.ResolveField(BitConverter.ToInt32(il, i + 3));
            MethodBase call = module.ResolveMethod(BitConverter.ToInt32(il, i + 10));
            if (field.Name == "SideOpaque" && field.DeclaringType == typeof(Block)
                && call.Name == "get_Item" && call.DeclaringType?.Name == "SmallBoolArray")
            {
                found = true;
                break;
            }
        }

        Assert.True(found, "CalculateVisibleFaces lost the ldloc.s 14 / ldflda SideOpaque / ldloc.s 15 / get_Item / stloc.s 16 sequence that Eco Machina anchors on.");
    }

    [Fact]
    public void CalculateVisibleFacesShipsThroughCecil()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string cecilList = File.ReadAllText(FindRepositoryFile("patches/cecil-owned.list"));

        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"CalculateVisibleFaces\", 4", programSource);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch", cecilList);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
#endif
