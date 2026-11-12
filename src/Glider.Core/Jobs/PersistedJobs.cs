using System.Text.Json.Serialization;
using Glider.Core.Contracts;

namespace Glider.Core.Jobs;

/// <summary>state/jobs.json.</summary>
internal sealed class PersistedJobFile
{
    public int Version { get; set; } = 1;

    public List<PersistedJob> Jobs { get; set; } = [];
}

/// <summary>Where a multi-step job is, so a restart resumes the right step (e.g. the post-install verify, not the download).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobStep>))]
internal enum JobStep
{
    Main,
    Verify,
    Repair,
}

internal sealed class PersistedJob
{
    public JobInfo Info { get; set; } = null!;

    public JobRequest Request { get; set; } = null!;

    public JobStep Step { get; set; }

    /// <summary>Install only: the game folder did not exist before this job, so cancelling may delete it.</summary>
    public bool CreatedGameFolder { get; set; }

    /// <summary>Install only: the folder the game is being installed into (base path + catalog folder name).</summary>
    public string? GameFolder { get; set; }

    /// <summary>Automatic repair already attempted after a failed verify (never loop).</summary>
    public bool AutoRepairAttempted { get; set; }

    /// <summary>Progress bookkeeping that spans pause/resume/restart (Legendary restarts its counters every run).</summary>
    public ProgressBaseline Baseline { get; set; } = new();

    public PersistedJob Clone() => new()
    {
        Info = Info,
        Request = Request,
        Step = Step,
        CreatedGameFolder = CreatedGameFolder,
        GameFolder = GameFolder,
        AutoRepairAttempted = AutoRepairAttempted,
        Baseline = Baseline.Clone(),
    };
}

internal sealed class ProgressBaseline
{
    /// <summary>Compressed bytes the first download run of the current step reported as "Download size".</summary>
    public long? TotalDownloadBytes { get; set; }

    /// <summary>Bytes the first run had to write (install size minus unchanged), for the written-bytes bar.</summary>
    public long? TotalWriteBytes { get; set; }

    /// <summary>Highest overall percent shown so far for the current step (keeps the bar monotonic).</summary>
    public double Percent { get; set; }

    /// <summary>Active seconds accumulated by earlier runs.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>Phase whose percent <see cref="Percent"/> measures (verify and download percentages are separate bars).</summary>
    public JobPhase? Phase { get; set; }

    public ProgressBaseline Clone() => (ProgressBaseline)MemberwiseClone();
}
