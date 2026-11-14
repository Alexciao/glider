using System.Diagnostics;
using Glider.Core.Contracts;
using Glider.Core.Legendary;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// <c>installed.json.lock</c> contention with two REAL Legendary processes (docs/research/legendary-v0.2.md §1): one sits at an
/// uninstall prompt holding the lock, Glider's own commands run meanwhile. Writers must answer "busy" (the job queue retries), never "failed";
/// readers and launch must not be held up.
/// </summary>
[Collection(RealLegendaryCollection.Name)]
public sealed class RealLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts `uninstall` without -y: it takes the installed lock and then waits for an answer on stdin.</summary>
    private static async Task<Process> HoldTheLockAsync(RealLegendaryEnv env)
    {
        var holder = env.StartRaw(["uninstall", VerifyFixture.App], redirectInput: true);
        var seen = new System.Text.StringBuilder();
        var buffer = new char[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        while (!seen.ToString().Contains("[y/N]", StringComparison.Ordinal))
        {
            if (await holder.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token) == 0)
            {
                throw new InvalidOperationException("the lock holder exited early: " + seen);
            }

            seen.Append(buffer[0]);
        }

        return holder;
    }

    private static async Task ReleaseAsync(Process holder)
    {
        await holder.StandardInput.WriteLineAsync("n"); // "Aborting..." and exit: the OS drops the lock with the process
        await holder.WaitForExitAsync(Ct);
        holder.Dispose();
    }

    [RealLegendaryFact]
    public async Task WritersAreBusyWhileAnotherLegendaryHoldsTheLockAndSucceedAfterItLetsGo()
    {
        using var env = new RealLegendaryEnv();
        VerifyFixture.Install(env);
        using var cli = env.Cli();
        var holder = await HoldTheLockAsync(env);
        try
        {
            var uninstall = (await cli.UninstallAsync(VerifyFixture.App, keepFiles: true, Ct)).Error;
            var move = (await cli.MoveAsync(VerifyFixture.App, env.Root, skipMove: true, null, Ct)).Error;
            var import = (await cli.ImportAsync("Other", Path.Combine(env.Games, VerifyFixture.App), Ct)).Error;
            var install = (await cli.InstallAsync(new InstallArgs(VerifyFixture.App, InstallMode.Install, env.Games, 0, 0, null), new Progress<LegendaryProgress>(), Ct)).Error;

            foreach (var (name, error) in new[] { ("uninstall", uninstall), ("move", move), ("import", import), ("install", install) })
            {
                Assert.True(error?.Code == ErrorCode.LegendaryLocked, $"{name}: {error?.Code} {error?.Detail}");
            }

            Assert.Contains("Failed to acquire installed data lock", uninstall!.Detail, StringComparison.Ordinal);
            Assert.NotNull(await cli.GetInstalledAsync(VerifyFixture.App, Ct)); // nothing was changed behind the holder's back
        }
        finally
        {
            await ReleaseAsync(holder);
        }

        var retry = await cli.UninstallAsync(VerifyFixture.App, keepFiles: true, Ct);
        Assert.True(retry.Success, retry.Error?.Detail);
    }

    [RealLegendaryFact]
    public async Task ReadersAndLaunchAreNotHeldUpByTheInstalledLock()
    {
        using var env = new RealLegendaryEnv();
        VerifyFixture.Install(env);
        env.WriteFakeUser();
        using var cli = env.Cli();
        var holder = await HoldTheLockAsync(env);
        try
        {
            var verify = await cli.VerifyAsync(VerifyFixture.App, new Progress<LegendaryProgress>(), Ct);
            var launch = await cli.GetLaunchCommandAsync(VerifyFixture.App, true, "", offline: true, Ct);
            var account = await cli.GetAccountOfflineAsync(Ct);

            Assert.True(verify.Success, verify.Error?.Detail);
            Assert.EndsWith("Game.exe", launch.Executable, StringComparison.Ordinal);
            Assert.Equal(RealLegendaryEnv.FakeDisplayName, account?.DisplayName);
        }
        finally
        {
            await ReleaseAsync(holder);
        }
    }

    [RealLegendaryFact]
    public async Task AQueuedUninstallWaitsOutTheLockAndThenRuns()
    {
        // The job queue's busy-retry path ("installed data lock" refusals are retried, never failed) end to end with the real exe.
        await using var h = await RealRuntimeHarness.StartAsync();
        var holder = await HoldTheLockAsync(h.Env);
        JobInfo job;
        try
        {
            job = await h.Rpc.Uninstall(new UninstallRequest(h.App, UninstallMode.GliderOnly));
            var blocked = await RealRuntimeHarness.WaitFor(() => h.Job(job.Id), j => j.BlockedReason is not null, "the job to report the busy lock");
            Assert.Equal(Errors.LegendaryLocked().Message, blocked.BlockedReason);
            Assert.NotEqual(JobStatus.Failed, blocked.Status);
            Assert.NotNull((await h.Env.Cli().GetInstalledAsync(h.App, Ct))); // still installed while the holder owns the lock
        }
        finally
        {
            await ReleaseAsync(holder);
        }

        var done = await h.WaitForDone(job.Id, "the uninstall after the lock was released");

        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        Assert.Null(done.BlockedReason);
        await h.Runtime.Service.LibraryForTests.FlushAsync();
        Assert.Equal(InstallStatus.NotInstalled, h.Game()!.InstallStatus);
        Assert.True(File.Exists(Path.Combine(h.Folder, "Game.exe"))); // Glider-only: files stay
    }
}
