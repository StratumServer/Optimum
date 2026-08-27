using System.Globalization;

namespace Optimum.Bootstrap.Core.Prerequisites;

/// <summary>
/// A four-part ilspycmd version and the range check that decides whether a
/// decompiler is close enough to the tested revision that the fixup passes in
/// <c>scripts/fix-base-ctor-calls.py</c> and <c>scripts/fix-closure-class.pl</c>
/// still apply. Ports <c>ilspycmd_version_supported</c> and its comparators from
/// <c>scripts/install-linux.sh</c>: a version with a prerelease suffix
/// (<c>-preview3</c>, <c>-rc1</c>) is rejected outright because it is not
/// <c>major.minor.patch.build</c>.
/// </summary>
public readonly record struct IlspycmdVersion(int Major, int Minor, int Patch, int Build)
    : IComparable<IlspycmdVersion>
{
    public static bool TryParse(string? text, out IlspycmdVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Split('.');
        if (parts.Length != 4)
            return false;

        int[] numbers = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        version = new IlspycmdVersion(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    public int CompareTo(IlspycmdVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        return Build.CompareTo(other.Build);
    }

    public static bool operator <(IlspycmdVersion a, IlspycmdVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(IlspycmdVersion a, IlspycmdVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(IlspycmdVersion a, IlspycmdVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(IlspycmdVersion a, IlspycmdVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}.{Build}";
}

/// <summary>The accepted ilspycmd range plus the pinned version, both read from <c>.config/</c>.</summary>
public readonly record struct IlspycmdCompatibility(IlspycmdVersion Minimum, IlspycmdVersion Maximum, string Pin)
{
    /// <summary>The hard-coded fallback in <c>scripts/install-linux.sh</c> when the config files are missing.</summary>
    public static readonly IlspycmdCompatibility Fallback = new(
        new IlspycmdVersion(10, 1, 0, 8386),
        new IlspycmdVersion(10, 1, 1, 8388),
        "10.1.1.8388");

    public bool Supports(string? version) =>
        IlspycmdVersion.TryParse(version, out var parsed) && parsed >= Minimum && parsed <= Maximum;
}
