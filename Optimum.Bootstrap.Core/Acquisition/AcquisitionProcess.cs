using System.Text;
using CliWrap;
using CliWrap.EventStream;
using Optimum.Bootstrap.Core.Build;

namespace Optimum.Bootstrap.Core.Acquisition;

/// <summary>
/// Runs a short acquisition subprocess (a dotnet-install script, a
/// <c>dotnet tool update</c>) and streams its output to an
/// <see cref="IBuildObserver"/>. A spawn failure comes back as a negative exit
/// code with the message on <see cref="Outcome.Message"/> rather than an
/// exception, so callers map every failure the same way.
/// </summary>
internal static class AcquisitionProcess
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    internal readonly record struct Outcome(int ExitCode, string? Message)
    {
        public bool Ok => ExitCode == 0;
    }

    internal static async Task<Outcome> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        IBuildObserver observer,
        CancellationToken cancellationToken)
    {
        int exitCode = -1;
        Command command = Cli.Wrap(executable)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);
        if (environment is not null)
            command = command.WithEnvironmentVariables(environment);

        try
        {
            await foreach (CommandEvent commandEvent in
                command.ListenAsync(Utf8, Utf8, cancellationToken, CancellationToken.None))
            {
                switch (commandEvent)
                {
                    case StandardOutputCommandEvent stdout:
                        observer.RawOutput(false, stdout.Text);
                        break;
                    case StandardErrorCommandEvent stderr:
                        observer.RawOutput(false, stderr.Text);
                        break;
                    case ExitedCommandEvent exited:
                        exitCode = exited.ExitCode;
                        break;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new Outcome(-1, $"could not start '{executable}': {ex.Message}");
        }

        return new Outcome(exitCode, exitCode == 0 ? null : $"'{executable}' exited {exitCode}");
    }
}
