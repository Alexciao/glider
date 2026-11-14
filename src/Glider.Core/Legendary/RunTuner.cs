using System.Runtime.Versioning;
using Glider.Core.Contracts;
using Glider.Core.Jobs;
using Glider.Core.Services;

namespace Glider.Core.Legendary;

/// <summary>What a download mode asks of one process (docs/download-perf.md §5.6).</summary>
internal static class ModeSettings
{
    /// <summary>
    /// Full: everything normal. Balanced: the file writer (and, for single-process commands, the whole tree) at below-normal CPU, low
    /// I/O and low memory priority; everyone else normal CPU and I/O but low memory priority, so the network side matches Full.
    /// Background: the whole tree at idle CPU, very-low I/O and memory priority, in EcoQoS.
    /// </summary>
    public static ProcessSettings For(DownloadMode mode, bool isWriter, bool wholeTree) => mode switch
    {
        DownloadMode.Full => ProcessSettings.Normal,
        DownloadMode.Balanced when isWriter || wholeTree => new ProcessSettings(CpuPriority.BelowNormal, IoPriority.Low, MemoryPriority.Low, EcoQos: false),
        DownloadMode.Balanced => new ProcessSettings(CpuPriority.Normal, IoPriority.Normal, MemoryPriority.Low, EcoQos: false),
        _ => new ProcessSettings(CpuPriority.Idle, IoPriority.VeryLow, MemoryPriority.VeryLow, EcoQos: true),
    };
}

/// <summary>
/// Applies the download mode to a running Legendary tree by PID. Legendary's multiprocessing children appear seconds after the
/// start, so the tuner re-enumerates the job (a few times a second at first, then every few seconds) and applies what is missing; a
/// mode change re-applies at once. The writer is the DLManager's last child (<see cref="ProcessRoles"/>): its pid comes from the
/// "Download Manager running with process-id" line, and it exists once the DLManager has <c>workers + 1</c> direct children.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RunTuner : IDisposable
{
    private readonly WindowsJobObject _job;
    private readonly RunControl _control;
    private readonly bool _wholeTree;
    private readonly int? _workers;
    private readonly ILog _log;
    private readonly Func<IReadOnlyList<ProcessEntry>> _snapshot;
    private readonly Dictionary<int, (ProcessSettings Settings, long? Created)> _applied = [];
    private readonly SemaphoreSlim _kick = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly long _started = Environment.TickCount64;
    private int _downloadManager;
    private long _writerLineAt;
    private int _writerPid;

    public RunTuner(WindowsJobObject job, RunControl control, bool wholeTree, int? workers, ILog log, Func<IReadOnlyList<ProcessEntry>>? snapshot = null)
    {
        _job = job;
        _control = control;
        _wholeTree = wholeTree;
        _workers = workers;
        _log = log;
        _snapshot = snapshot ?? Game.ProcessSnapshot.Take;
        _control.Changed += Kick;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>The file-writer's pid once identified (0 before; diagnostics and tests).</summary>
    public int WriterPid => Volatile.Read(ref _writerPid);

    public void SetDownloadManager(int pid)
    {
        Volatile.Write(ref _downloadManager, pid);
        Kick();
    }

    public void WriterStarting()
    {
        Interlocked.CompareExchange(ref _writerLineAt, Environment.TickCount64, 0);
        Kick();
    }

    public void Kick()
    {
        try
        {
            _kick.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _control.Changed -= Kick;
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
        _kick.Dispose();
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Apply();
                var young = Environment.TickCount64 - _started < 45_000;
                await _kick.WaitAsync(TimeSpan.FromMilliseconds(young ? 750 : 5000), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.Warn("legendary", "Process tuning stopped.", e);
        }
    }

    /// <summary>One pass: find the processes of the job and give each the settings of the current mode if it doesn't have them yet.</summary>
    internal void Apply()
    {
        var pids = ProcessTuning.JobProcessIds(_job.Handle);
        if (pids.Count == 0)
        {
            return;
        }

        var mode = _control.Mode;
        var writer = Writer(pids);
        foreach (var pid in pids)
        {
            var settings = ModeSettings.For(mode, writer.Contains(pid), _wholeTree);
            var created = ProcessTuning.CreationTicks(pid);
            if (_applied.TryGetValue(pid, out var last) && last.Settings == settings && last.Created == created)
            {
                continue; // already so, and not a reused pid
            }

            if (ProcessTuning.TryApply(pid, settings))
            {
                _applied[pid] = (settings, created);
            }
        }
    }

    private IReadOnlySet<int> Writer(IReadOnlyList<int> pids)
    {
        var dlm = Volatile.Read(ref _downloadManager);
        var lineAt = Interlocked.Read(ref _writerLineAt);
        if (_wholeTree || dlm == 0 || lineAt == 0)
        {
            return new HashSet<int>();
        }

        var snapshot = _snapshot();
        var direct = snapshot.Count(p => p.ParentId == dlm && pids.Contains(p.Id));
        var settled = _workers is { } w ? direct >= w + 1 : Environment.TickCount64 - lineAt >= 1500;
        if (!settled)
        {
            return new HashSet<int>();
        }

        var writer = ProcessRoles.Writer(pids, dlm, snapshot, ProcessTuning.CreationTicks);
        if (writer.Count > 0 && Volatile.Read(ref _writerPid) == 0)
        {
            var root = snapshot.Where(p => writer.Contains(p.Id) && p.ParentId == dlm).Select(p => p.Id).FirstOrDefault();
            Volatile.Write(ref _writerPid, root);
            _log.Info("legendary", $"File writer identified: pid {root} (download manager {dlm}).");
        }

        return writer;
    }
}
