using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>Log export: a diagnostics header (no tokens, no account ids) + the redacted log tail.</summary>
internal static partial class Diagnostics
{
    public static string Header(AppSnapshot snap, GameRecord? record)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("==== Glider diagnostics ====");
        sb.AppendLine(ci, $"Glider:     {snap.App.Version}");
        sb.AppendLine(ci, $"Legendary:  {(snap.App.LegendaryVersion.Length > 0 ? snap.App.LegendaryVersion : "unknown")}");
        sb.AppendLine(ci, $"OS:         {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), Win11={snap.App.IsWindows11}");
        sb.AppendLine(ci, $".NET:       {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(ci, $"Hardware:   {snap.App.CpuCount} CPUs, {Format.Bytes(snap.App.TotalMemoryBytes)} RAM; auto workers={snap.App.AutoWorkers}, shm={snap.App.AutoSharedMemoryMb} MB");
        sb.AppendLine(ci, $"Warm:       {snap.App.Warm}");
        sb.AppendLine(ci, $"Auth:       {snap.Auth.Status}");
        var g = snap.Game;
        sb.AppendLine(ci, $"Game:       {g.InstallStatus}, run={g.RunState}, latest={g.LatestVersion ?? "?"}, updateAvailable={g.UpdateAvailable}, lastCheck={g.LastUpdateCheck?.ToString("u", ci) ?? "never"}");
        if (record is not null)
        {
            sb.AppendLine(ci, $"Install:    {record.InstallPath} v{record.Version} ({Format.Bytes(record.InstallSizeBytes)}), adoptedFromEgl={record.AdoptedFromEgl}, eglShared={record.EglInstallationGuid is not null}, needsVerification={record.NeedsVerification}");
        }

        var e = snap.Egl;
        sb.AppendLine(ci, $"EGL:        installed={e.LauncherInstalled}, running={e.LauncherRunning}, busy={e.BusyWithGame}, sync={e.SyncEnabled}/{e.SyncAvailable}, candidates={e.Candidates.Count}");
        foreach (var c in e.Candidates)
        {
            sb.AppendLine(ci, $"            - {c.InstallPath} v{c.Version ?? "?"} exists={c.FolderExists} exe={c.ExecutableFound} stale={c.IsStale}");
        }

        foreach (var j in snap.Jobs)
        {
            sb.AppendLine(string.Create(ci, $"Job:        {j.Kind} {j.Status} {j.Error?.Code} {j.BlockedReason}").TrimEnd());
        }

        sb.AppendLine(ci, $"Settings:   {JsonSerializer.Serialize(snap.Settings, GliderJsonContext.Default.Settings)}");
        sb.AppendLine("============================");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// The newest lines of the host shell's own log (<c>host-*.log</c>: window, WebView2 and the Epic login window's navigation/HTTP status
    /// trail), so one "Copy logs" / app.getLogs carries both. Empty when there is none.
    /// </summary>
    public static string HostLogTail(string logDirectory, int maxLines)
    {
        try
        {
            var file = Directory.EnumerateFiles(logDirectory, "host-*.log").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (file is null)
            {
                return "";
            }

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var lines = new Queue<string>();
            while (sr.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }

            return lines.Count == 0 ? "" : Environment.NewLine + "--- host log ---" + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Defense in depth: strips anything shaped like a secret even if a component logged one.</summary>
    public static string Redact(string text) => Infrastructure.SecretRedactor.Redact(text);
}

/// <summary>Third-party notices. Texts come from &lt;LicensesDirectory&gt;/&lt;id&gt;.txt when the build ships them.</summary>
internal static class Licenses
{
    private sealed record Entry(string Id, string Name, string Version, string License, string Url);

    private static readonly Entry[] Items =
    [
        new("legendary", "Legendary", "0.21.1", "GPL-3.0-or-later", "https://github.com/legendary-gl/legendary/tree/0.21.1"),
        new("dotnet", ".NET Runtime", "10.0", "MIT", "https://github.com/dotnet/runtime"),
        new("webview2", "Microsoft Edge WebView2", "", "Microsoft Software License", "https://www.nuget.org/packages/Microsoft.Web.WebView2"),
        new("react", "React", "19", "MIT", "https://github.com/facebook/react"),
        new("radix", "Radix UI Primitives", "", "MIT", "https://github.com/radix-ui/primitives"),
        new("motion", "Motion", "", "MIT", "https://github.com/motiondivision/motion"),
        new("zustand", "Zustand", "", "MIT", "https://github.com/pmndrs/zustand"),
        new("tailwindcss", "Tailwind CSS", "4", "MIT", "https://github.com/tailwindlabs/tailwindcss"),
        new("inter", "Inter (font)", "", "OFL-1.1", "https://github.com/rsms/inter"),
        new("velopack", "Velopack", "", "MIT", "https://github.com/velopack/velopack"),
    ];

    public static LicensesResult Load(string directory) => new(Items
        .Select(i => new LicenseInfo(i.Name, i.Version, i.License, i.Url, ReadText(directory, i.Id)))
        .ToList());

    private static string? ReadText(string dir, string id)
    {
        try
        {
            var f = Path.Combine(dir, id + ".txt");
            return File.Exists(f) ? File.ReadAllText(f) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
