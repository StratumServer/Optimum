#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Xunit;

namespace Optimum.Tests;

public class TreeGenPooledDimensionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VintageStory.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void TreeGenRuntimePatch_RefreshesPooledBranchAndVineDimensions()
    {
        string patch = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "patches/runtime/VSEssentials/Vintagestory/ServerMods/TreeGen.cs.patch"));

        Assert.Contains("positionStack ??= new BlockPos[32];", patch);
        Assert.Contains("blockPos.dimension = basePos.dimension;", patch);
        Assert.Contains("vineScratchPos.dimension = currentPos.dimension;", patch);
    }

    [Fact]
    public void AssembledRuntimeDonor_TreeGenReuseAcrossDimensions_UsesRequestedDimensionForAllAccessesAndPlacements()
    {
        string root = RepoRoot();
        string vanillaPath = Path.Combine(root, ".vanilla/win-x64/runtime-donors/Mods/VSEssentials.dll");
        string compiledPath = Path.Combine(root, ".build/runtime-donors/VSEssentials/bin/Release/net10.0/VSEssentials.dll");
        if (!File.Exists(vanillaPath) || !File.Exists(compiledPath))
        {
            return;
        }

        string tempOutput = Path.Combine(Path.GetTempPath(), $"optimum_treegen_dim_{Guid.NewGuid():N}.dll");
        TreeGenLoadContext? alc = null;
        try
        {
            Assert.True(Optimum.Patcher.ModPatcher.Patch("vsessentials", vanillaPath, compiledPath, tempOutput));
            Assert.True(File.Exists(tempOutput));

            alc = new TreeGenLoadContext(tempOutput);
            Assembly patchedAsm = alc.LoadFromAssemblyPath(tempOutput);

            Type treeGenType = patchedAsm.GetType("Vintagestory.ServerMods.TreeGen")!;
            Type configType = patchedAsm.GetType("Vintagestory.ServerMods.NoObf.TreeGenConfig")!;
            Type trunkType = patchedAsm.GetType("Vintagestory.ServerMods.NoObf.TreeGenTrunk")!;
            Type branchType = patchedAsm.GetType("Vintagestory.ServerMods.NoObf.TreeGenBranch")!;
            Type blocksType = patchedAsm.GetType("Vintagestory.ServerMods.NoObf.TreeGenBlocks")!;
            Type forestFloorType = patchedAsm.GetType("Vintagestory.ServerMods.ForestFloorSystem")!;

            object blocks = Activator.CreateInstance(blocksType)!;
            blocksType.GetField("logBlockId")!.SetValue(blocks, 1);
            blocksType.GetField("leavesBlockId")!.SetValue(blocks, 2);
            blocksType.GetField("leavesBranchyBlockId")!.SetValue(blocks, 3);
            blocksType.GetField("leavesBranchyDeadBlockId")!.SetValue(blocks, 3);
            blocksType.GetField("leavesByLevel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(blocks, new[] { new[] { 2 }, new[] { 3 } });
            blocksType.GetField("mossDecorBlock")!.SetValue(blocks, new Block { BlockId = 99 });
            blocksType.GetField("vinesBlock")!.SetValue(blocks, new Block { BlockId = 100 });
            blocksType.GetField("vinesEndBlock")!.SetValue(blocks, new Block { BlockId = 101 });

            object trunk = Activator.CreateInstance(trunkType)!;
            trunkType.GetField("widthMultiplier")!.SetValue(trunk, 0.8f);
            trunkType.GetField("widthloss")!.SetValue(trunk, 0.08f);
            trunkType.GetField("dieAt")!.SetValue(trunk, NatFloat.createUniform(0.05f, 0f));
            trunkType.GetField("angleVert")!.SetValue(trunk, NatFloat.createUniform(0f, 0f));
            trunkType.GetField("angleHori")!.SetValue(trunk, NatFloat.createUniform(0f, 0f));
            trunkType.GetField("branchStart")!.SetValue(trunk, NatFloat.createUniform(0.15f, 0f));
            trunkType.GetField("branchSpacing")!.SetValue(trunk, NatFloat.createUniform(0.25f, 0f));
            trunkType.GetField("branchWidthMultiplier")!.SetValue(trunk, NatFloat.createUniform(0.6f, 0f));
            trunkType.GetField("branchQuantity")!.SetValue(trunk, NatFloat.createUniform(1f, 0f));
            trunkType.GetField("branchVerticalAngle")!.SetValue(trunk, NatFloat.createUniform(1.0f, 0f));
            trunkType.GetField("branchHorizontalAngle")!.SetValue(trunk, NatFloat.createUniform(0.5f, 0f));
            trunkType.GetField("probability")!.SetValue(trunk, 1f);

            object subBranch = Activator.CreateInstance(branchType)!;
            branchType.GetField("widthMultiplier")!.SetValue(subBranch, 0.6f);
            branchType.GetField("widthloss")!.SetValue(subBranch, 0.1f);
            branchType.GetField("dieAt")!.SetValue(subBranch, NatFloat.createUniform(0.05f, 0f));
            branchType.GetField("branchStart")!.SetValue(subBranch, NatFloat.createUniform(2f, 0f));
            branchType.GetField("branchWidthMultiplier")!.SetValue(subBranch, NatFloat.createUniform(0f, 0f));
            branchType.GetField("branchQuantity")!.SetValue(subBranch, NatFloat.createUniform(0f, 0f));

            Array trunkArray = Array.CreateInstance(trunkType, 1);
            trunkArray.SetValue(trunk, 0);
            Array branchArray = Array.CreateInstance(branchType, 1);
            branchArray.SetValue(subBranch, 0);

            object config = Activator.CreateInstance(configType)!;
            configType.GetField("sizeMultiplier")!.SetValue(config, 1f);
            configType.GetField("sizeVar")!.SetValue(config, NatFloat.Zero);
            configType.GetField("heightMultiplier")!.SetValue(config, 1f);
            configType.GetField("trunks")!.SetValue(config, trunkArray);
            configType.GetField("branches")!.SetValue(config, branchArray);
            configType.GetField("treeBlocks")!.SetValue(config, blocks);

            object forestFloor = RuntimeHelpers.GetUninitializedObject(forestFloorType);
            ITreeGenerator treeGen = (ITreeGenerator)Activator.CreateInstance(treeGenType, config, 42, forestFloor)!;

            var accessor = DispatchProxy.Create<IBlockAccessor, RecordingBlockAccessorProxy>();
            var proxy = (RecordingBlockAccessorProxy)(object)accessor;

            var treeParams = new TreeGenParams
            {
                size = 1f,
                mossGrowthChance = 1f,
                vinesGrowthChance = 1f,
                skipForestFloor = true,
                hemisphere = EnumHemisphere.North,
            };

            foreach (int expectedDim in new[] { 0, 2, 0 })
            {
                proxy.RecordedPosCalls.Clear();
                treeGen.GrowTree(accessor, new BlockPos(256, 64, 256, expectedDim), treeParams, new LCGRandom(1234 + expectedDim));

                Assert.Contains(proxy.RecordedPosCalls, c => c.Method == nameof(IBlockAccessor.SetBlock));
                Assert.Contains(proxy.RecordedPosCalls, c => c.Method == nameof(IBlockAccessor.GetBlockId));
                Assert.Contains(proxy.RecordedPosCalls, c => c.Method == nameof(IBlockAccessor.SetDecor));
                Assert.Contains(proxy.RecordedPosCalls, c => c.Method == nameof(IBlockAccessor.GetBlock));
                Assert.All(proxy.RecordedPosCalls, c => Assert.Equal(expectedDim, c.Dimension));
            }

            var posStack = (BlockPos?[]?)treeGenType.GetField("positionStack", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(treeGen);
            Assert.NotNull(posStack);
            Assert.NotNull(posStack![0]);
            Assert.NotNull(posStack[1]);
            Assert.NotNull(treeGenType.GetField("vineScratchPos", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(treeGen));
        }
        finally
        {
            alc?.Unload();
            if (File.Exists(tempOutput))
            {
                try { File.Delete(tempOutput); } catch { }
            }
            string pdb = Path.ChangeExtension(tempOutput, ".pdb");
            if (File.Exists(pdb))
            {
                try { File.Delete(pdb); } catch { }
            }
        }
    }

    private sealed class TreeGenLoadContext : AssemblyLoadContext
    {
        private readonly string _patchedEssentialsPath;

        public TreeGenLoadContext(string patchedEssentialsPath)
            : base("TreeGenRuntimeDonorTest", isCollectible: true)
        {
            _patchedEssentialsPath = patchedEssentialsPath;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "VSEssentials", StringComparison.OrdinalIgnoreCase))
            {
                return LoadFromAssemblyPath(_patchedEssentialsPath);
            }
            return null;
        }
    }

    public class RecordingBlockAccessorProxy : DispatchProxy
    {
        private static readonly Block AirBlock = new() { BlockId = 0, Replaceable = 9000, SideSolid = default };
        private static readonly Block WoodBlock = new() { BlockId = 1, BlockMaterial = EnumBlockMaterial.Wood, Replaceable = 0 };

        public List<(string Method, int Dimension)> RecordedPosCalls { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
            {
                return null;
            }

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] is BlockPos pos)
                    {
                        RecordedPosCalls.Add((targetMethod.Name, pos.dimension));
                    }
                }
            }

            return targetMethod.Name switch
            {
                "get_MapSizeX" or "get_MapSizeZ" => 512,
                "get_MapSizeY" => 256,
                "GetBlockId" => 0,
                "GetBlock" when args is [int blockId] => blockId == 0 ? AirBlock : WoodBlock,
                "GetBlock" or "GetBlockOnSide" => AirBlock,
                _ => targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null,
            };
        }
    }
}
