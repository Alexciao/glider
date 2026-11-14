using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Legendary;

namespace Glider.Core.Services;

/// <summary>Log export: a diagnostics header (no tokens, no account ids) + the redacted log tail.</summary>
internal static partial class Diagnostics
{
    public static string Header(AppSnapshot snap, IReadOnlyDictionary<string, GameSummary> library, IReadOnlyDictionary<string, GameEntry> games)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("==== Glider diagnostics ====");
        sb.AppendLine(ci, $"Glider:     {snap.App.Version}{(snap.App.MigratedFrom is { } m ? $" (migrated from {m})" : "")}");
        sb.AppendLine(ci, $"Legendary:  {(snap.App.LegendaryVersion.Length > 0 ? snap.App.LegendaryVersion : "unknown")}");
        sb.AppendLine(ci, $"OS:         {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), Win11={snap.App.IsWindows11}");
        sb.AppendLine(ci, $".NET:       {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(ci, $"Hardware:   {snap.App.CpuCount} CPUs, {Format.Bytes(snap.App.TotalMemoryBytes)} RAM; auto workers={snap.App.AutoWorkers}, shm={snap.App.AutoSharedMemoryMb} MB");
        sb.AppendLine(ci, $"Warm:       {snap.App.Warm}");
        sb.AppendLine(ci, $"Auth:       {snap.Auth.Status}, connectivity={snap.Connectivity}");
        var l = snap.Library;
        sb.AppendLine(ci, $"Library:    {l.GameCount} items (rev {l.Revision}), updates={l.UpdatesAvailable}, syncing={l.Syncing}, lastSync={l.LastSyncedAt?.ToString("u", ci) ?? "never"}{(l.SyncError is { } se ? $", syncError={se.Code}" : "")}");
        foreach (var g in library.Values.Where(g => g.InstallPath is not null).OrderBy(g => g.AppName, StringComparer.Ordinal))
        {
            var rec = games.TryGetValue(g.AppName, out var e) ? e.Install : null;
            sb.AppendLine(ci, $"Install:    {g.AppName}: {g.InstallStatus} {g.InstallPath} v{g.InstalledVersion} latest={g.LatestVersion ?? "?"} update={g.UpdateAvailable} run={g.RunState}, eglShared={g.SharedWithEgl}, needsVerification={g.NeedsVerification}, adopted={rec?.AdoptedFromEgl ?? false}");
        }

        foreach (var r in snap.Sessions)
        {
            sb.AppendLine(ci, $"Session:    {r.AppName} {r.State} since={r.Since?.ToString("u", ci) ?? "-"} offline={r.Offline}");
        }

        var eg = snap.Egl;
        sb.AppendLine(ci, $"EGL:        installed={eg.LauncherInstalled}, running={eg.LauncherRunning}, busy=[{string.Join(',', eg.BusyApps)}], sync={eg.SyncEnabled}, installs={eg.Installs.Count}");
        foreach (var i in eg.Installs)
        {
            sb.AppendLine(ci, $"            - {i.AppName} {i.InstallPath} v{i.Version ?? "?"} {i.Status} drift={i.Drift}");
        }

        var q = snap.Queue;
        sb.AppendLine(ci, $"Queue:      paused={q.Paused}, mode={q.EffectiveMode}{(q.ModeReason is { } mr ? $" ({mr})" : "")}");
        foreach (var j in q.Jobs)
        {
            sb.AppendLine(string.Create(ci, $"Job:        {j.Kind} {j.AppName} {j.Status} {j.Phase} {j.Error?.Code} {j.BlockedReason}").TrimEnd());
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

    /// <summary>The Glider fork of Legendary is modified GPLv3 software: say so, and say where the patch and the source offer are.</summary>
    internal const string ForkNotice = "This build of Glider bundles a modified Legendary (the Glider fork: CDN failover, a runtime control file and clearer progress output). " +
                                        "The complete changes are in legendary/glider-legendary.patch next to legendary.exe, and legendary/SOURCE-OFFER.txt says how to get the corresponding source. " +
                                        "Both are free software under the GNU General Public License v3 or later.";

    public static LicensesResult Load(string directory) => new(Items
        .Select(i => i.Id == "legendary" && LegendaryPin.IsFork
            ? new LicenseInfo("Legendary (modified by Glider)", LegendaryPin.Version ?? i.Version, i.License, i.Url, ForkNotice + "\n\n" + ReadText(directory, i.Id))
            : new LicenseInfo(i.Name, i.Version, i.License, i.Url, ReadText(directory, i.Id)))
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
