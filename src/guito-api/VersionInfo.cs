using System.Reflection;

namespace GuitoApi;

/// <summary>
/// Build-stamped version (issue #75). CI injects the version string at publish
/// time via <c>dotnet publish -p:InformationalVersion=…</c> (computed by
/// <c>deploy/version.sh</c>: semver derived from the last <c>v*</c> git tag,
/// pre-release suffix, build counter, UTC timestamp, short SHA — see the
/// deploy/version.sh header for the exact scheme). Nothing is hand-edited in
/// the repo; the version is an artifact of the build, never runtime state.
/// A local, unstamped build falls back to the csproj's default informational
/// version, so the value is never empty or unknown-shaped.
/// </summary>
public static class VersionInfo
{
    public static string Version { get; } =
        typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0-unknown";
}
