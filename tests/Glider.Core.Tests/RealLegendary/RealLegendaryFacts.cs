using System.Runtime.CompilerServices;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// The opt-in switch for the real-Legendary suite (docs/testing.md). Set <c>GLIDER_REAL_LEGENDARY</c> to the path of a legendary.exe, or to
/// <c>1</c> to use the repository's <c>src/Glider.App/legendary/legendary.exe</c> (<c>dotnet run build/FetchLegendary.cs</c>). Without it every
/// test in this folder is reported as skipped and nothing is spawned. <c>GLIDER_REAL_LEGENDARY_NET=1</c> additionally enables the few tests
/// that talk to Epic's public endpoints with a deliberately invalid token (never a real account).
/// </summary>
internal static class RealLegendary
{
    public const string ExeVariable = "GLIDER_REAL_LEGENDARY";
    public const string NetVariable = "GLIDER_REAL_LEGENDARY_NET";

    public const string SkipReason = "Real Legendary is not enabled: set GLIDER_REAL_LEGENDARY=<path to legendary.exe> (or 1 for the repository copy), see docs/testing.md.";

    public static string? ExePath { get; } = Resolve();

    public static bool NetworkEnabled { get; } = Environment.GetEnvironmentVariable(NetVariable) is "1" or "true";

    private static string? Resolve()
    {
        var value = Environment.GetEnvironmentVariable(ExeVariable)?.Trim().Trim('"');
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value is "1" or "true")
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "Glider.App", "legendary", "legendary.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        return File.Exists(value) ? Path.GetFullPath(value) : null;
    }
}

/// <summary>A test that needs the real legendary.exe (skipped when <see cref="RealLegendary.ExePath"/> is unset).</summary>
internal sealed class RealLegendaryFactAttribute : FactAttribute
{
    public RealLegendaryFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "legendary.exe is a Windows binary.";
        }
        else if (RealLegendary.ExePath is null)
        {
            Skip = RealLegendary.SkipReason;
        }
    }
}

/// <summary>A real-Legendary test that also needs the public Epic endpoints (invalid token only) and <see cref="RealLegendary.NetVariable"/>.</summary>
internal sealed class RealLegendaryNetworkFactAttribute : FactAttribute
{
    public RealLegendaryNetworkFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "legendary.exe is a Windows binary.";
        }
        else if (RealLegendary.ExePath is null)
        {
            Skip = RealLegendary.SkipReason;
        }
        else if (!RealLegendary.NetworkEnabled)
        {
            Skip = "Needs the network: set GLIDER_REAL_LEGENDARY_NET=1.";
        }
    }
}
