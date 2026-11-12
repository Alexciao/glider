using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Glider.Core.Contracts;

namespace Glider.Core.Legendary;

/// <summary>Sizes from the install summary (cli.py:1042-1047); all bytes, null when not printed.</summary>
internal readonly record struct LegendaryRunSizes(long? DownloadBytes, long? InstallBytes, long? UnchangedBytes, long? ReusableBytes);

/// <summary>
/// Optional richer progress channel. When the <see cref="IProgress{T}"/> handed to <see cref="ILegendary"/> also
/// implements this, Legendary's phase changes and summary sizes are reported too (the job queue uses it; the public
/// seam stays <see cref="LegendaryProgress"/>).
/// </summary>
internal interface ILegendaryProgressSink : IProgress<LegendaryProgress>
{
    void OnPhase(JobPhase phase);

    void OnSizes(LegendaryRunSizes sizes);
}

internal sealed record LegendaryCommand(LegendaryCommandKind Kind, IReadOnlyList<string> Arguments)
{
    /// <summary>Mutating commands share one global lock: Legendary allows a single installer at a time (lock file) and
    /// concurrent logins could race on refresh-token rotation.</summary>
    public bool Exclusive { get; init; }

    public bool CaptureStdout { get; init; }

    /// <summary>stdout carries secrets (launch --json has a live exchange code): never log or keep it in the tail.</summary>
    public bool SensitiveStdout { get; init; }

    /// <summary>Argument indexes replaced by *** in logs.</summary>
    public IReadOnlyCollection<int> RedactedArguments { get; init; } = [];

    public TimeSpan? Timeout { get; init; }

    /// <summary>Kill the run when neither progress nor meaningful output changed for this long (retry spam doesn't count).</summary>
    public TimeSpan? StallTimeout { get; init; }

    /// <summary>Run below normal priority (long downloads/hashing must not starve the foreground).</summary>
    public bool LowPriority { get; init; }

    /// <summary>Kill the run early when the facts so far show it is doomed (e.g. SDL data failed on a fresh install).</summary>
    public Func<LegendaryOutputFacts, bool>? AbortWhen { get; init; }

    public string Display()
    {
        var parts = Arguments.Select((a, i) => RedactedArguments.Contains(i) ? "***" : a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a);
        return string.Join(' ', parts);
    }
}

internal sealed record LegendaryRunResult(
    int ExitCode,
    string Stdout,
    IReadOnlyList<string> Tail,
    LegendaryOutputFacts Facts,
    bool Stalled,
    bool TimedOut,
    bool AbortedEarly,
    TimeSpan Duration,
    TimeSpan StallTimeout);

public sealed class LegendaryRunnerOptions
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Lines of non-progress output kept for error details.</summary>
    public int TailLines { get; init; } = 200;

    /// <summary>Extra environment for the child (tests use it to drive the fake Legendary).</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>SHA-256 (hex) the exe must have; checked before the first run and again whenever the file changes. Null = skip.</summary>
    public string? ExpectedSha256 { get; init; }
}

/// <summary>
/// Spawns <see cref="AppPaths.LegendaryExe"/> with an isolated <c>LEGENDARY_CONFIG_PATH</c>, closed stdin and no window,
/// inside a kill-on-close Job Object on Windows (process-tree kill elsewhere). Output is pumped asynchronously,
/// decoded per stream (UTF-8 stdout, ANSI + backslashreplace stderr), parsed as spans, and accumulated into
/// <see cref="LegendaryOutputFacts"/>; cancellation kills the whole tree.
/// </summary>
internal sealed class LegendaryProcessRunner : IDisposable
{
    private static readonly string[] ScrubbedEnvironment =
        ["LEGENDARY_WRAPPER_EXE", "LGDRY_WRAPPER", "LGDRY_WINE_BINARY", "LGDRY_WINE_PREFIX", "LGDRY_NO_WINE", "XDG_CONFIG_HOME"];

    private readonly AppPaths _paths;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly LegendaryRunnerOptions _options;
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private int _active;

    public LegendaryProcessRunner(AppPaths paths, ILog log, TimeProvider? time = null, LegendaryRunnerOptions? options = null)
    {
        _paths = paths;
        _log = log;
        _time = time ?? TimeProvider.System;
        _options = options ?? new LegendaryRunnerOptions();
    }

    /// <summary>Number of Legendary processes currently alive (config.ini is only written at zero).</summary>
    public int ActiveProcesses => Volatile.Read(ref _active);

    public void Dispose()
    {
        _exclusive.Dispose();
        _configLock.Dispose();
    }

    public async Task<LegendaryRunResult> RunAsync(LegendaryCommand command, IProgress<LegendaryProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(_paths.LegendaryExe))
        {
            throw new GliderException(Errors.LegendaryMissing($"Not found: {_paths.LegendaryExe}"));
        }

        await EnsureIntactAsync(ct).ConfigureAwait(false);

        if (command.Exclusive)
        {
            await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await _configLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_active == 0)
                {
                    try
                    {
                        if (await LegendaryConfigFile.EnsureAsync(_paths.LegendaryConfig, ct).ConfigureAwait(false))
                        {
                            _log.Debug("legendary", "Wrote config.ini.");
                        }
                    }
                    catch (IOException e)
                    {
                        _log.Warn("legendary", "Could not update config.ini; continuing with the existing one.", e);
                    }
                }

                Interlocked.Increment(ref _active);
            }
            finally
            {
                _configLock.Release();
            }

            try
            {
                return await RunProcessAsync(command, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
        finally
        {
            if (command.Exclusive)
            {
                _exclusive.Release();
            }
        }
    }

    private readonly Lock _verifiedLock = new();
    private (long Length, DateTime WriteUtc)? _verified;

    /// <summary>A truncated or swapped exe otherwise surfaces as "the task failed, retry", which can never succeed.</summary>
    private async Task EnsureIntactAsync(CancellationToken ct)
    {
        if (_options.ExpectedSha256 is not { Length: > 0 } expected)
        {
            return;
        }

        var info = new FileInfo(_paths.LegendaryExe);
        var stamp = (info.Length, info.LastWriteTimeUtc);
        bool known;
        lock (_verifiedLock)
        {
            known = _verified == stamp;
        }

        if (known)
        {
            return;
        }

        string actual;
        try
        {
            var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                actual = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new GliderException(Errors.LegendaryMissing($"Can't read {_paths.LegendaryExe}: {e.Message}"), e);
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            _log.Error("legendary", $"{_paths.LegendaryExe} is damaged or was replaced: SHA-256 {actual}, expected {expected}.");
            throw new GliderException(Errors.LegendaryMissing($"legendary.exe doesn't match the copy shipped with Glider (SHA-256 {actual[..12]}…)."));
        }

        lock (_verifiedLock)
        {
            _verified = stamp;
        }
    }

    private async Task<LegendaryRunResult> RunProcessAsync(LegendaryCommand command, IProgress<LegendaryProgress>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_paths.LegendaryExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _paths.LegendaryConfig, // never the game folder: a cwd handle would block moving/deleting it
        };
        foreach (var a in command.Arguments)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var name in ScrubbedEnvironment)
        {
            psi.Environment.Remove(name);
        }

        psi.Environment["LEGENDARY_CONFIG_PATH"] = _paths.LegendaryConfig;
        if (_options.ExtraEnvironment is { } extra)
        {
            foreach (var (k, v) in extra)
            {
                psi.Environment[k] = v;
            }
        }

        var state = new RunState(command, progress as ILegendaryProgressSink, progress, _log, _options.TailLines, _time);
        var started = _time.GetTimestamp();
        var display = command.Display();
        _log.Info("legendary", $"> legendary {display}");

        WindowsJobObject? job = null;
        using var process = new Process { StartInfo = psi };
        try
        {
            if (OperatingSystem.IsWindows())
            {
                job = WindowsJobObject.TryCreate(command.LowPriority, _log);
            }

            try
            {
                process.Start();
            }
            catch (Win32Exception e)
            {
                throw new GliderException(Errors.LegendaryMissing($"{_paths.LegendaryExe}: {e.Message}"), e);
            }

            AttachToJob(process, job, command.LowPriority);
            try
            {
                process.StandardInput.Close(); // any stray input() gets EOF instead of hanging forever
            }
            catch (IOException)
            {
            }

            using var pumpCts = new CancellationTokenSource();
            var stdoutPump = new LinePump(LegendaryEncoding.Stdout, unescapePython: false, line => state.OnLine(line, stdout: true));
            var stderrPump = new LinePump(LegendaryEncoding.Stderr, unescapePython: true, line => state.OnLine(line, stdout: false));
            var pumps = Task.WhenAll(
                Task.Run(() => stdoutPump.RunAsync(process.StandardOutput.BaseStream, pumpCts.Token), CancellationToken.None),
                Task.Run(() => stderrPump.RunAsync(process.StandardError.BaseStream, pumpCts.Token), CancellationToken.None));

            using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stallTimeout = command.StallTimeout;
            var timeout = command.Timeout ?? _options.DefaultTimeout;
            var exited = process.WaitForExitAsync(CancellationToken.None);
            var reason = await WatchAsync(exited, state, stallTimeout, timeout, started, watchdogCts.Token).ConfigureAwait(false);

            if (reason == StopReason.Exited)
            {
                // Legendary is done; anything still in the job is an orphan (e.g. a DLManager worker) that would also
                // keep our pipes open. Reap it now rather than waiting for the job handle to close.
                if (OperatingSystem.IsWindows())
                {
                    job?.Terminate();
                }
            }
            else
            {
                KillTree(process, job);
                try
                {
                    await exited.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _log.Warn("legendary", "Legendary did not exit after being killed.");
                }
            }

            // Output may still be draining; a straggler that escaped the job could hold the pipe open, so bound the wait.
            try
            {
                await pumps.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await pumpCts.CancelAsync().ConfigureAwait(false);
                _log.Warn("legendary", "Output pipes stayed open after exit; abandoning them.");
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }

            var duration = _time.GetElapsedTime(started); // the process' lifetime, not including slow consumers
            await state.FinishAsync().ConfigureAwait(false);
            var exitCode = process.HasExited ? process.ExitCode : -1;
            _log.Info("legendary", $"< legendary {command.Kind}: exit {exitCode} after {duration.TotalSeconds:0.0} s{(reason == StopReason.Exited ? "" : $" ({reason})")}");

            if (reason == StopReason.Cancelled)
            {
                throw new OperationCanceledException(ct);
            }

            return new LegendaryRunResult(exitCode, state.Stdout, state.TailSnapshot(), state.Facts,
                Stalled: reason == StopReason.Stalled,
                TimedOut: reason == StopReason.TimedOut,
                AbortedEarly: reason == StopReason.Aborted,
                duration,
                stallTimeout ?? TimeSpan.Zero);
        }
        finally
        {
            state.CompleteEvents(); // no-op after FinishAsync; releases the dispatcher if we got here by an exception
            if (!process.HasExitedSafe())
            {
                KillTree(process, job);
            }

            if (OperatingSystem.IsWindows())
            {
                job?.Dispose(); // KILL_ON_JOB_CLOSE also reaps orphans (e.g. a DLManager whose parent died)
            }
        }
    }

    private enum StopReason
    {
        Exited,
        Cancelled,
        Stalled,
        TimedOut,
        Aborted,
    }

    private async Task<StopReason> WatchAsync(Task exited, RunState state, TimeSpan? stall, TimeSpan timeout, long started, CancellationToken ct)
    {
        var basis = stall ?? (timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(8));
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(basis.TotalMilliseconds / 8, 50, 1000));
        while (true)
        {
            try
            {
                var delay = Task.Delay(tick, _time, ct);
                var done = await Task.WhenAny(exited, delay, state.AbortRequested).ConfigureAwait(false);
                if (done == exited)
                {
                    return StopReason.Exited;
                }

                if (done == state.AbortRequested)
                {
                    return StopReason.Aborted;
                }

                await delay.ConfigureAwait(false); // surfaces cancellation
            }
            catch (OperationCanceledException)
            {
                return exited.IsCompleted ? StopReason.Exited : StopReason.Cancelled;
            }

            var now = _time.GetTimestamp();
            if (timeout > TimeSpan.Zero && _time.GetElapsedTime(started, now) > timeout)
            {
                return StopReason.TimedOut;
            }

            if (stall is { } s && _time.GetElapsedTime(state.LastLiveness, now) > s)
            {
                _log.Warn("legendary", $"No progress for {s.TotalSeconds:0} s; killing Legendary (resume keeps completed files).");
                return StopReason.Stalled;
            }
        }
    }

    private void AttachToJob(Process process, WindowsJobObject? job, bool lowPriority)
    {
        var assigned = false;
        if (OperatingSystem.IsWindows() && job is not null)
        {
            assigned = job.TryAssign(process);
            if (!assigned)
            {
                _log.Warn("legendary", "Could not assign Legendary to the job object; falling back to tree kill.");
            }
        }

        if (lowPriority && !assigned)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal; // inherited by children on Windows
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
            }
        }
    }

    private void KillTree(Process process, WindowsJobObject? job)
    {
        // Walk the tree while the root is still alive, then terminate the job for anything that escaped the walk.
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            _log.Debug("legendary", $"Tree kill: {e.Message}");
        }

        if (OperatingSystem.IsWindows())
        {
            job?.Terminate();
        }
    }

    private enum RunEventKind
    {
        Progress,
        Phase,
        Sizes,
    }

    private readonly record struct RunEvent(RunEventKind Kind, LegendaryProgress Progress, JobPhase Phase, LegendaryRunSizes Sizes);

    /// <summary>Per-run mutable state; the two pump threads call <see cref="OnLine"/> concurrently, so everything is under one lock.</summary>
    private sealed class RunState
    {
        private readonly Lock _lock = new();
        private readonly LegendaryCommand _command;
        private readonly ILegendaryProgressSink? _sink;
        private readonly IProgress<LegendaryProgress>? _progress;
        private readonly ILog _log;
        private readonly TimeProvider _time;
        private readonly ProgressParser _parser = new();
        private readonly Queue<string> _tail = new();
        private readonly int _tailLines;
        private readonly StringBuilder _stdout = new();
        private readonly TaskCompletionSource _abort = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Parsed results go to consumers through a channel: a slow progress subscriber (UI bridge, persistence) must never
        // stall the pump, or the pipe fills up and Legendary's own logging — and with it the download — blocks.
        private readonly Channel<RunEvent> _events = Channel.CreateUnbounded<RunEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly Task _dispatcher;
        private LegendaryProgress _lastPublished;
        private bool _havePublished;
        private JobPhase _phase = JobPhase.Preparing;
        private bool _sizesReported;
        private int _noiseLogged;
        private long _lastProgressLog;
        private long _lastLiveness;

        public RunState(LegendaryCommand command, ILegendaryProgressSink? sink, IProgress<LegendaryProgress>? progress, ILog log, int tailLines, TimeProvider time)
        {
            _command = command;
            _sink = sink;
            _progress = progress;
            _log = log;
            _tailLines = tailLines;
            _time = time;
            _lastLiveness = time.GetTimestamp();
            _dispatcher = progress is null ? Task.CompletedTask : Task.Run(DispatchAsync);
        }

        public LegendaryOutputFacts Facts { get; } = new();

        public Task AbortRequested => _abort.Task;

        public long LastLiveness => Interlocked.Read(ref _lastLiveness);

        public string Stdout
        {
            get
            {
                lock (_lock)
                {
                    return _stdout.ToString();
                }
            }
        }

        public IReadOnlyList<string> TailSnapshot()
        {
            lock (_lock)
            {
                return [.. _tail];
            }
        }

        public void OnLine(ReadOnlySpan<char> line, bool stdout)
        {
            lock (_lock)
            {
                if (_parser.TryParse(line, out var published))
                {
                    if (published)
                    {
                        Publish();
                    }

                    return;
                }

                var cls = Facts.Observe(line, stdout);
                if (cls == LineClass.Normal)
                {
                    Touch();
                }

                var secret = stdout && _command.SensitiveStdout;
                if (stdout && _command.CaptureStdout)
                {
                    _stdout.Append(line).Append('\n');
                }

                if (!secret)
                {
                    var text = new string(line);
                    _tail.Enqueue(text);
                    if (_tail.Count > _tailLines)
                    {
                        _tail.Dequeue();
                    }

                    if (cls == LineClass.Normal || _noiseLogged++ < 10 || _noiseLogged % 200 == 0)
                    {
                        _log.Info("legendary", (stdout ? "out| " : "err| ") + text);
                    }
                }

                SetPhase(Facts.Phase);
                if (!_sizesReported && Facts.SummaryComplete)
                {
                    _sizesReported = true;
                    Post(new RunEvent(RunEventKind.Sizes, default, default,
                        new LegendaryRunSizes(Facts.DownloadSizeBytes, Facts.InstallSizeBytes, Facts.UnchangedBytes, Facts.ReusableBytes)));
                }

                if (_command.AbortWhen?.Invoke(Facts) == true)
                {
                    _abort.TrySetResult();
                }
            }
        }

        /// <summary>Publishes a trailing partial block and waits until every event reached the consumers.</summary>
        public Task FinishAsync()
        {
            lock (_lock)
            {
                if (_parser.TryFlush())
                {
                    Publish();
                }
            }

            _events.Writer.TryComplete();
            return _dispatcher;
        }

        public void CompleteEvents() => _events.Writer.TryComplete();

        private void Post(RunEvent e)
        {
            if (_progress is not null)
            {
                _events.Writer.TryWrite(e);
            }
        }

        private async Task DispatchAsync()
        {
            await foreach (var e in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    switch (e.Kind)
                    {
                        case RunEventKind.Progress:
                            _progress!.Report(e.Progress);
                            break;
                        case RunEventKind.Phase:
                            _sink?.OnPhase(e.Phase);
                            break;
                        case RunEventKind.Sizes:
                            _sink?.OnSizes(e.Sizes);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("legendary", "A progress consumer threw; continuing.", ex);
                }
            }
        }

        private void Publish()
        {
            var p = _parser.Snapshot;
            var kind = _parser.SnapshotKind;
            if (!_havePublished || p.ProcessedChunks != _lastPublished.ProcessedChunks || p.DownloadedBytes != _lastPublished.DownloadedBytes ||
                p.WrittenBytes != _lastPublished.WrittenBytes || Math.Abs(p.Percent - _lastPublished.Percent) > 0.0001)
            {
                Touch(); // only real movement counts as liveness; identical blocks during a stall don't
            }

            _havePublished = true;
            _lastPublished = p;
            if (kind == ProgressKind.Download && Facts.Phase is JobPhase.Preparing or JobPhase.Verifying)
            {
                Facts.Phase = JobPhase.Downloading;
            }
            else if (kind == ProgressKind.Verify && Facts.Phase != JobPhase.Verifying)
            {
                Facts.Phase = JobPhase.Verifying;
            }

            SetPhase(Facts.Phase);
            Post(new RunEvent(RunEventKind.Progress, p, default, default));

            var now = _time.GetTimestamp();
            if (_lastProgressLog == 0 || _time.GetElapsedTime(_lastProgressLog, now) > TimeSpan.FromSeconds(30))
            {
                _lastProgressLog = now;
                _log.Info("legendary", kind == ProgressKind.Verify
                    ? $"verify {p.Percent:0.0}% ({p.ProcessedChunks}/{p.TotalChunks} files)"
                    : $"download {p.Percent:0.00}% ({p.ProcessedChunks}/{p.TotalChunks}), {p.DownloadedBytes / 1048576.0:0} MiB, {p.DownloadBytesPerSecond / 1048576.0:0.0} MiB/s");
            }
        }

        private void SetPhase(JobPhase phase)
        {
            if (phase != _phase)
            {
                _phase = phase;
                Post(new RunEvent(RunEventKind.Phase, default, phase, default));
            }
        }

        private void Touch() => Interlocked.Exchange(ref _lastLiveness, _time.GetTimestamp());
    }
}

internal static class ProcessExtensions
{
    public static bool HasExitedSafe(this Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true; // never started
        }
    }
}
