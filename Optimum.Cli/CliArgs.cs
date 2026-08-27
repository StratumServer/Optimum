namespace Optimum.Cli;

/// <summary>
/// A small flag parser: <c>--flag value</c> for flags named in
/// <paramref name="valueFlags"/>, <c>--switch</c> for the rest. Positional
/// arguments and unknown value-flag usage are collected as errors rather than
/// thrown, so a verb can turn them into one <c>bad-input</c> result.
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _switches = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];

    public CliArgs(IReadOnlyList<string> args, ISet<string> valueFlags)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                _errors.Add($"unexpected argument: {token}");
                continue;
            }

            if (valueFlags.Contains(token))
            {
                if (i + 1 >= args.Count)
                {
                    _errors.Add($"{token} needs a value");
                    break;
                }
                _options[token] = args[++i];
            }
            else
            {
                _switches.Add(token);
            }
        }
    }

    public bool Has(string name) => _switches.Contains(name);

    public string? Get(string name) => _options.TryGetValue(name, out string? value) ? value : null;

    public IReadOnlyList<string> Errors => _errors;
}
