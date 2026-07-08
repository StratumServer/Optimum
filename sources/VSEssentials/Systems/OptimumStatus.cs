using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
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
            .EndSubCommand();
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
        sb.Append(OptimumDiagnostics.GetChiselLodSummary());
        return sb.ToString();
    }
}
