using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Vintagestory.GameContent;

public class OptimumStatusModSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
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
