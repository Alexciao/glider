using Glider.Core.Contracts;

namespace Glider.Core.Jobs;

/// <summary>
/// Where one step of a job sits on the whole-job bar. A Legendary run reports a verify percentage and/or a download
/// percentage (repair = verify then download); each maps to its own slice so the job's percent only ever grows.
/// </summary>
internal readonly record struct JobSegment(double VerifyFrom, double VerifyTo, double DownloadFrom, double DownloadTo, bool Repairing = false)
{
    public static JobSegment Download(double from, double to) => new(from, from, from, to);

    public static JobSegment Verify(double from, double to) => new(from, to, to, to);

    public (double From, double To) For(JobPhase measuring) => measuring == JobPhase.Verifying ? (VerifyFrom, VerifyTo) : (DownloadFrom, DownloadTo);
}

/// <summary>Planned phases per job kind (the UI's segmented stepper) and the bar slices of each step.</summary>
internal static class JobPlan
{
    public static IReadOnlyList<JobPhase> Phases(JobKind kind) => kind switch
    {
        JobKind.Install => [JobPhase.Preparing, JobPhase.Downloading, JobPhase.Installing, JobPhase.Verifying, JobPhase.Finalizing],
        JobKind.Update or JobKind.Modify => [JobPhase.Preparing, JobPhase.Downloading, JobPhase.Installing, JobPhase.Finalizing],
        JobKind.Repair or JobKind.Verify => [JobPhase.Verifying, JobPhase.Repairing, JobPhase.Finalizing],
        JobKind.Move => [JobPhase.Preparing, JobPhase.Moving, JobPhase.Finalizing],
        JobKind.Uninstall => [JobPhase.Removing],
        JobKind.Import => [JobPhase.Importing, JobPhase.Finalizing],
        JobKind.Overlay => [JobPhase.Preparing, JobPhase.Downloading, JobPhase.Installing, JobPhase.Finalizing],
        _ => [JobPhase.Preparing],
    };

    public static JobSegment Segment(JobKind kind, JobStep step) => (kind, step) switch
    {
        (JobKind.Install, JobStep.Main) => JobSegment.Download(0, 90),
        (JobKind.Install, JobStep.Verify) => JobSegment.Verify(90, 98),
        (JobKind.Install, JobStep.Repair) => new JobSegment(98, 98, 98, 99.5, Repairing: true),
        (JobKind.Update or JobKind.Modify, JobStep.Main) => JobSegment.Download(0, 96),
        (JobKind.Update or JobKind.Modify, JobStep.Verify) => JobSegment.Verify(96, 98.5),
        (JobKind.Update or JobKind.Modify, JobStep.Repair) => new JobSegment(98.5, 98.5, 98.5, 99.5, Repairing: true),
        // `legendary repair`: its own verify pass, then the download of what's bad.
        (JobKind.Repair, _) => new JobSegment(0, 35, 35, 99, Repairing: true),
        // Verify job: verify, repair (download only), verify again.
        (JobKind.Verify, JobStep.Main) => JobSegment.Verify(0, 45),
        (JobKind.Verify, JobStep.Repair) => new JobSegment(45, 45, 45, 85, Repairing: true),
        (JobKind.Verify, JobStep.Verify) => JobSegment.Verify(85, 99),
        (JobKind.Move, _) => JobSegment.Download(0, 99),
        _ => JobSegment.Download(0, 99),
    };

    public static bool CanPause(JobKind kind) => kind is JobKind.Install or JobKind.Update or JobKind.Repair or JobKind.Verify or JobKind.Modify;

    /// <summary>Kinds whose Legendary run logs in and refreshes metadata first (the M lane must not overlap that phase).</summary>
    public static bool HasPreDownloadPhase(JobKind kind) => kind is JobKind.Install or JobKind.Update or JobKind.Repair or JobKind.Verify or JobKind.Import or JobKind.Modify;
}

/// <summary>What the queue must respect besides its own jobs: holds while a game runs, and the effective download mode.</summary>
public interface IQueuePolicy
{
    /// <summary>Non-null = don't start (and stop) pausable writer jobs, e.g. "Paused while Fortnite is running".</summary>
    string? HoldReason { get; }

    DownloadMode EffectiveMode { get; }

    /// <summary>Why the effective mode differs from Settings.DownloadMode (null when it doesn't).</summary>
    string? ModeReason { get; }
}

/// <summary>
/// Seam for the per-mode resource plumbing (docs/v0.2-design.md §4, Phase 2): given the effective download mode, how a
/// writer-lane Legendary run is spawned. The default keeps v0.1's behaviour for every mode.
/// </summary>
public interface IResourcePolicy
{
    ResourceProfile For(DownloadMode mode, JobKind kind);
}

/// <summary>v0.1 behaviour: download/verify runs below normal CPU priority (Job Object priority-class limit), whatever the mode.</summary>
public sealed class DefaultResourcePolicy : IResourcePolicy
{
    public static DefaultResourcePolicy Instance { get; } = new();

    public ResourceProfile For(DownloadMode mode, JobKind kind) => ResourceProfile.Default;
}

/// <summary>
/// Shared between the writer lane and the metadata lane: a writer job is in its pre-download phase (spawned, logging in and
/// refreshing the same metadata files) until its first progress line or exit. Metadata-lane commands wait for it to pass.
/// </summary>
public sealed class WriterActivity
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _idle = Done();

    public bool InPreDownload
    {
        get
        {
            lock (_gate)
            {
                return !_idle.Task.IsCompleted;
            }
        }
    }

    public void BeginPreDownload()
    {
        lock (_gate)
        {
            if (_idle.Task.IsCompleted)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void EndPreDownload()
    {
        lock (_gate)
        {
            _idle.TrySetResult();
        }
    }

    public Task WhenNotPreDownloadAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return _idle.Task.WaitAsync(ct);
        }
    }

    private static TaskCompletionSource Done()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }
}

/// <summary>≈1 Hz graph samples per job, downsampled (pairwise averages) to at most <see cref="Max"/> points.</summary>
internal sealed class JobSamples
{
    public const int Max = 600;

    private readonly Lock _gate = new();
    private readonly List<JobSample> _samples = [];
    private double _lastT = double.NegativeInfinity;
    private double _spacing = 1;

    public void Add(JobSample s)
    {
        lock (_gate)
        {
            if (s.T - _lastT < _spacing * 0.95)
            {
                return;
            }

            _lastT = s.T;
            _samples.Add(s);
            if (_samples.Count > Max)
            {
                var halved = new List<JobSample>(_samples.Count / 2 + 1);
                for (var i = 0; i + 1 < _samples.Count; i += 2)
                {
                    var a = _samples[i];
                    var b = _samples[i + 1];
                    halved.Add(new JobSample(b.T, (a.NetworkBytesPerSecond + b.NetworkBytesPerSecond) / 2, (a.DiskBytesPerSecond + b.DiskBytesPerSecond) / 2));
                }

                if (_samples.Count % 2 == 1)
                {
                    halved.Add(_samples[^1]);
                }

                _samples.Clear();
                _samples.AddRange(halved);
                _spacing *= 2;
            }
        }
    }

    public IReadOnlyList<JobSample> Snapshot()
    {
        lock (_gate)
        {
            return [.. _samples];
        }
    }
}
