using System.IO;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Vintagestory.GameContent;

public class OptimumStatusModSystem : ModSystem
{
    private ICoreClientAPI api;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        this.api = api;
        api.ChatCommands.GetOrCreate("optimum")
            .WithDescription(Lang.Get("optimum-cmd-description"))
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("status")
                .WithDescription(Lang.Get("optimum-cmd-status"))
                .HandleWith(_ => TextCommandResult.Success(BuildStatus()))
            .EndSubCommand()
            .BeginSubCommand("reset")
                .WithDescription(Lang.Get("optimum-cmd-reset"))
                .HandleWith(_ =>
                {
                    OptimumDiagnostics.ResetAllCounters();
                    return TextCommandResult.Success(Lang.Get("optimum-cmd-reset-done"));
                })
            .EndSubCommand()
            .BeginSubCommand("chisel")
                .WithDescription("Chisel LOD diagnostics")
                .BeginSubCommand("lodstats")
                    .WithDescription("Show chisel LOD counters")
                    .HandleWith(_ =>
                    {
                        string summary = OptimumDiagnostics.GetChiselLodSummary();
                        api.Logger.Notification("[Optimum] chisel lodstats:\n" + summary);
                        return TextCommandResult.Success(summary);
                    })
                .EndSubCommand()
                .BeginSubCommand("lodreset")
                    .WithDescription("Reset chisel LOD counters")
                    .HandleWith(_ =>
                    {
                        OptimumDiagnostics.ResetChiselLod();
                        api.Logger.Notification("[Optimum] chisel LOD diagnostics reset");
                        return TextCommandResult.Success("Chisel LOD diagnostics reset.");
                    })
                .EndSubCommand()
                .BeginSubCommand("dumpmesh")
                    .WithDescription("Export chisel MeshData from the current chunk for the offline harness")
                    .HandleWith(_ => DumpChiselMeshes())
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("chunk")
                .WithDescription("Chunk data capture for benchmarks")
                .BeginSubCommand("dumpblocks")
                    .WithDescription("Export block data from the current chunk for the greedy meshing harness")
                    .HandleWith(_ => DumpChunkBlocks())
                .EndSubCommand()
            .EndSubCommand()
            .BeginSubCommand("greedy")
                .WithDescription("Greedy mesh diagnostics")
                .BeginSubCommand("stats")
                    .WithDescription("Show greedy mesh counters")
                    .HandleWith(_ =>
                    {
                        string summary = OptimumDiagnostics.GetGreedyMeshSummary();
                        api.Logger.Notification("[Optimum] greedy stats:\n" + summary);
                        return TextCommandResult.Success(summary);
                    })
                .EndSubCommand()
                .BeginSubCommand("reset")
                    .WithDescription("Reset greedy mesh counters")
                    .HandleWith(_ =>
                    {
                        OptimumDiagnostics.ResetGreedyMesh();
                        api.Logger.Notification("[Optimum] greedy mesh diagnostics reset");
                        return TextCommandResult.Success("Greedy mesh diagnostics reset.");
                    })
                .EndSubCommand()
            .EndSubCommand();
    }

    private TextCommandResult DumpChunkBlocks()
    {
        var plr = api.World.Player.Entity;
        var plrPos = plr.Pos.AsBlockPos;
        int chunkSize = GlobalConstants.ChunkSize;
        int cx = plrPos.X / chunkSize;
        int cy = plrPos.Y / chunkSize;
        int cz = plrPos.Z / chunkSize;

        string outDir = Path.Combine(api.DataBasePath, "benchmarks", "chunks");
        Directory.CreateDirectory(outDir);

        string filename = $"chunk_{cx}_{cy}_{cz}.bin";
        string filePath = Path.Combine(outDir, filename);

        int totalBlocks = chunkSize * chunkSize * chunkSize;
        using var fs = new FileStream(filePath, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        // Header: chunk size and position
        bw.Write(chunkSize);
        bw.Write(cx); bw.Write(cy); bw.Write(cz);

        // Per-block data: blockId, drawType, faceCullMode, sideOpaque (packed byte),
        // renderPass, randomDrawOffset, shapeUsesColormap flag, textureSubIds (6 ints)
        int cubeCount = 0;
        int mergeEligible = 0;

        for (int y = 0; y < chunkSize; y++)
        for (int z = 0; z < chunkSize; z++)
        for (int x = 0; x < chunkSize; x++)
        {
            var pos = new BlockPos(cx * chunkSize + x, cy * chunkSize + y, cz * chunkSize + z);
            var block = api.World.BlockAccessor.GetBlock(pos);

            int blockId = block.BlockId;
            byte drawType = (byte)block.DrawType;
            byte faceCullMode = (byte)block.FaceCullMode;
            byte sideOpaquePacked = 0;
            for (int face = 0; face < 6; face++)
                if (block.SideOpaque[face]) sideOpaquePacked |= (byte)(1 << face);
            byte renderPass = (byte)block.RenderPass;
            byte randomDrawOffset = (byte)block.RandomDrawOffset;
            bool usesColormap = block.ShapeUsesColormap || block.LoadColorMapAnyway;

            bw.Write(blockId);
            bw.Write(drawType);
            bw.Write(faceCullMode);
            bw.Write(sideOpaquePacked);
            bw.Write(renderPass);
            bw.Write(randomDrawOffset);
            bw.Write(usesColormap);

            if (block.DrawType == EnumDrawType.Cube) cubeCount++;
            if (block.DrawType == EnumDrawType.Cube &&
                faceCullMode == (byte)EnumFaceCullMode.Default &&
                sideOpaquePacked == 0x3F &&
                randomDrawOffset == 0 &&
                !usesColormap)
                mergeEligible++;
        }

        string msg = $"Exported chunk ({cx},{cy},{cz}): {totalBlocks} blocks, {cubeCount} cubes, {mergeEligible} merge-eligible. File: {filename}";
        api.Logger.Notification("[Optimum] " + msg);
        return TextCommandResult.Success(msg);
    }

    private TextCommandResult DumpChiselMeshes()
    {
        var plr = api.World.Player.Entity;
        var plrPos = plr.Pos.AsBlockPos;
        int chunkSize = GlobalConstants.ChunkSize;
        int cx = plrPos.X / chunkSize;
        int cy = plrPos.Y / chunkSize;
        int cz = plrPos.Z / chunkSize;

        string outDir = Path.Combine(api.DataBasePath, "benchmarks", "meshes");
        Directory.CreateDirectory(outDir);
        int exported = 0;

        for (int lx = 0; lx < chunkSize; lx++)
        for (int ly = 0; ly < chunkSize; ly++)
        for (int lz = 0; lz < chunkSize; lz++)
        {
            var pos = new BlockPos(cx * chunkSize + lx, cy * chunkSize + ly, cz * chunkSize + lz);
            var be = api.World.BlockAccessor.GetBlockEntity(pos);
            if (be == null) continue;
            // ChiselBlock stores its mesh in BEBehaviorMicroBlock. Find by type name
            // since we reference VSEssentials but not VSSurvivalMod.
            BlockEntityBehavior mb = null;
            for (int b = 0; b < be.Behaviors.Count; b++)
            {
                if (be.Behaviors[b].GetType().Name == "BEBehaviorMicroBlock")
                {
                    mb = be.Behaviors[b];
                    break;
                }
            }
            if (mb == null) continue;

            // Trigger a mesh rebuild so we get fresh data, then grab it via reflection.
            // The mesh is a MeshData stored in a private field. This is dev-only tooling;
            // production code never calls this path.
            var meshField = mb.GetType().GetField("currentMesh",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (meshField == null) continue;
            var mesh = meshField.GetValue(mb) as MeshData;
            if (mesh == null || mesh.VerticesCount == 0) continue;

            string filename = $"chisel_{pos.X}_{pos.Y}_{pos.Z}.bin";
            string filePath = Path.Combine(outDir, filename);
            using var fs = new FileStream(filePath, FileMode.Create);
            using var bw = new BinaryWriter(fs);

            bw.Write(mesh.VerticesCount);
            bw.Write(mesh.IndicesCount);
            // Positions: xyz floats
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                bw.Write(mesh.xyz[i * 3]);
                bw.Write(mesh.xyz[i * 3 + 1]);
                bw.Write(mesh.xyz[i * 3 + 2]);
            }
            // Normals: packed int per vertex
            if (mesh.Normals != null)
                for (int i = 0; i < mesh.VerticesCount; i++)
                    bw.Write(mesh.Normals[i]);
            // UVs: uv floats
            if (mesh.Uv != null)
                for (int i = 0; i < mesh.VerticesCount; i++)
                {
                    bw.Write(mesh.Uv[i * 2]);
                    bw.Write(mesh.Uv[i * 2 + 1]);
                }
            // Indices
            for (int i = 0; i < mesh.IndicesCount; i++)
                bw.Write(mesh.Indices[i]);

            exported++;
        }

        string msg = $"Exported {exported} chisel mesh(es) to {outDir}";
        api.Logger.Notification("[Optimum] " + msg);
        return TextCommandResult.Success(msg);
    }

    private static string BuildStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Optimum v{OptimumConfig.Version}");
        foreach (var (name, value) in OptimumConfig.DescribeToggles())
        {
            sb.AppendLine($"  {name}: {value}");
        }
        sb.AppendLine(OptimumDiagnostics.GetCountersSummary());
        sb.AppendLine(OptimumDiagnostics.GetChiselLodSummary());
        sb.AppendLine(OptimumDiagnostics.GetAnimBlockSummary());
        sb.Append(OptimumDiagnostics.GetGreedyMeshSummary());
        return sb.ToString();
    }
}
