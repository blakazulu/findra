using System.Reflection;

namespace Findra;

/// <summary>
/// Which build this is, in a form the update check can compare.
///
/// <para>Spec 9b puts the version in <c>--searchprobe</c>, <c>--searchbench</c>, the log header
/// and the About section, and requires that comparison happen on parsed numbers rather than on
/// string ordering. The trap is upstream of the comparison: .NET's default
/// <c>InformationalVersion</c> carries a <c>+&lt;sha&gt;</c> suffix,
/// <see cref="System.Version.TryParse(string, out System.Version)"/> rejects it, and
/// <see cref="UpdateCheck.Compare"/> answers 0 for anything it cannot parse - which
/// <see cref="UpdateCheck.CheckAsync"/> reads as "up to date". A single stray character
/// therefore turns the whole feature into a permanent lie rather than into an error.</para>
/// </summary>
public static class BuildInfo
{
    /// <summary>The running build's version, e.g. <c>1.2.0</c>, or <c>?</c> when the assembly
    /// carries neither an informational nor an assembly version.</summary>
    public static string Version { get; } = Normalise(
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(BuildInfo).Assembly.GetName().Version?.ToString());

    /// <summary>
    /// The informational version with any build metadata removed, falling back to the assembly
    /// version's first three components.
    ///
    /// <para>A pre-release suffix is deliberately KEPT: dropping it would make a release candidate
    /// claim to be the release. Kept, it fails to parse, the check reports Unknown, and Unknown is
    /// the truth.</para>
    /// </summary>
    public static string Normalise(string? informational, string? assemblyVersion)
    {
        string? s = informational?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            int plus = s.IndexOf('+');
            return plus >= 0 ? s[..plus] : s;
        }

        // System.Version, spelled out: inside this class the bare name binds to the property above.
        if (System.Version.TryParse(assemblyVersion, out System.Version? v))
            return $"{v.Major}.{v.Minor}.{v.Build}";

        return "?";
    }
}
