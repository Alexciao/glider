using Glider.Core.Contracts;

namespace Glider.Core.Tests.Jobs;

/// <summary>The writer lane runs an Overlay job through the real LegendaryCli against the scripted fake exe.</summary>
public class OverlayJobTests
{
    private static JobRequest Overlay(QueueHarness h) =>
        new(JobInfo.OverlayAppName, JobKind.Overlay, Path.Combine(h.Env.Root, "overlay"), null, null, null, "Epic overlay");

    [Fact]
    public async Task AnOverlayInstallRunsWithProgressAndNeedsNoSignIn()
    {
        await using var h = new QueueHarness();
        h.Env.Script("eos-overlay-install", """
            download app=eos-overlay chunks=60 files=6 mib=12 interval=5
            err [cli] INFO: Finished downloading, setting up overlay...
            writefile overlay_install.json {"version":"1.2.3","install_path":"C:/glider-test/overlay"}
            """);
        var q = await h.StartAsync();

        var job = q.Enqueue(Overlay(h));
        var done = await QueueHarness.WaitFor(q, job.Id, j => j.Status == JobStatus.Completed, timeoutMs: 60_000);

        Assert.Equal(JobKind.Overlay, done.Kind);
        Assert.Equal(100, done.Progress?.Percent);
        Assert.Contains(h.Progress, p => p.JobId == job.Id && p.Percent is > 0 and < 100);
        Assert.Equal(["before:Overlay", "ok:Overlay"], h.Hooks.Calls);
        var (command, args) = Assert.Single(h.Env.Invocations());
        Assert.Equal("eos-overlay-install", command);
        Assert.Equal("--path", args[3]);
    }

    [Fact]
    public async Task AFailedOverlayJobKeepsItsError()
    {
        await using var h = new QueueHarness();
        h.Env.Script("eos-overlay-install", "err [cli] WARNING: The following exception occurred while waiting for the downloader to finish: ConnectionError('x')");
        var q = await h.StartAsync();

        var job = q.Enqueue(Overlay(h));
        var failed = await QueueHarness.WaitFor(q, job.Id, j => j.Status == JobStatus.Failed, timeoutMs: 60_000);

        Assert.Equal(ErrorCode.Network, failed.Error?.Code);
        Assert.Equal(["before:Overlay", "failed:Overlay:Network"], h.Hooks.Calls);
    }
}
