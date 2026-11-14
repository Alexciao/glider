namespace Glider.Core.Tests.Egl;

/// <summary>v0.1's candidate view of a scan (the EGL detection tests are written against it).</summary>
internal sealed record Cand(string Id, Glider.Core.Contracts.CandidateSource Source, string AppName, string InstallPath, string? Version,
    bool FolderExists, bool ExecutableFound, bool IsStale, long? SizeBytes, string? Reason);

internal static class EglScanCompat
{
    extension(EglScanResult s)
    {
        public IReadOnlyList<Cand> Candidates => [.. s.Installs.Select(ToCand)];

        public bool BusyWithGame => s.Installs.Any(i => i.Busy);

        public DateTimeOffset? LastScan => s.At;
    }

    public static Cand ToCand(EglFoundInstall i) => new(
        i.Id, i.Source, i.AppName, i.InstallPath, i.Version, i.FolderExists, i.LooksInstalled, !i.LooksInstalled, i.SizeBytes,
        i.LooksInstalled ? null : i.FolderExists ? "The game files are missing from this folder." : "This folder doesn't exist.");
}
