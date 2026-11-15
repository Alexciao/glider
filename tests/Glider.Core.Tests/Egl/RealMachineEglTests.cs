using System.Runtime.CompilerServices;
using Glider.Core.Egl;

namespace Glider.Core.Tests.Egl;

/// <summary>
/// Opt-in, read-only check against this PC's real Epic Games Launcher data (<c>GLIDER_REAL_EGL=1</c>). CI has no EGL, so the
/// fixture tests can only cover formats we've already seen; this is how a new EGL format (EOSH, Nov 2026) gets caught on a real
/// machine before a release. It reads C:\ProgramData\Epic only and writes nothing. Set <c>GLIDER_REAL_EGL_EXPECT</c> to an app name
/// (e.g. <c>Fortnite</c>) to fail when EGL has that game installed but Glider doesn't find it.
/// </summary>
public class RealMachineEglTests(ITestOutputHelper output)
{
    [RealEglFact]
    public void EveryInstallTheLauncherKnowsIsFound()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "glider-real-egl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var paths = new AppPaths(tmp, Path.Combine(tmp, "legendary.exe")); // never Glider's real data
            var svc = new EglService(EglEnvironment.ForMachine(), paths, new TestLog(), new FakeProcesses(), EglTestTree.FastOptions());
            var scan = svc.Scan(TestContext.Current.CancellationToken);

            output.WriteLine($"EGL installed: {scan.LauncherInstalled}, running: {scan.LauncherRunning}");
            foreach (var i in scan.Installs)
            {
                output.WriteLine($"{i.AppName} {i.Version} at {i.InstallPath} (folder exists: {i.FolderExists})");
            }

            if (Environment.GetEnvironmentVariable("GLIDER_REAL_EGL_EXPECT") is { Length: > 0 } expected)
            {
                Assert.Contains(scan.Installs, i => string.Equals(i.AppName, expected, StringComparison.OrdinalIgnoreCase) && i.FolderExists);
            }
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }
}

internal sealed class RealEglFactAttribute : FactAttribute
{
    public RealEglFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("GLIDER_REAL_EGL") != "1")
        {
            Skip = "Reads this PC's real Epic Games Launcher data: set GLIDER_REAL_EGL=1 (read-only).";
        }
    }
}
