using System.Text.Json;
using System.Text.Json.Serialization;
using Glider.Core.Contracts;

namespace Glider.Core.Jobs;

/// <summary>state/jobs.json, schema 2: the v0.1 shape plus appName/title/options per job, queue order and the pause-all flag.</summary>
internal sealed class PersistedJobFile
{
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>The whole queue is paused by the user.</summary>
    public bool Paused { get; set; }

    /// <summary>Queue order: unfinished jobs run in this order.</summary>
    public List<PersistedJob> Jobs { get; set; } = [];
}

/// <summary>Where a multi-step job is, so a restart resumes the right step (e.g. the post-install verify, not the download).</summary>
[JsonConverter(typeof(JobStepConverter))]
internal enum JobStep
{
    Main,
    Verify,
    Repair,
}

/// <summary>v0.1 wrote <c>"Main"|"Verify"|"Repair"</c> (PascalCase) while every other enum is camelCase: accept both, write camelCase.</summary>
internal sealed class JobStepConverter : JsonConverter<JobStep>
{
    public override JobStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<JobStep>(reader.GetString(), ignoreCase: true, out var step) && Enum.IsDefined(step))
        {
            return step;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n) && Enum.IsDefined((JobStep)n))
        {
            return (JobStep)n;
        }

        throw new JsonException("Unknown job step.");
    }

    public override void Write(Utf8JsonWriter writer, JobStep value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
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

    /// <summary>Verify jobs: files checked / found bad by the first verify, kept so a restart in the repair step can still report them.</summary>
    public int VerifyChecked { get; set; }

    public int VerifyBad { get; set; }

    /// <summary>Stopped (not paused) because a game started and Settings.WhilePlaying is Pause; resumes when the last game closes.</summary>
    public bool Held { get; set; }

    /// <summary>Progress bookkeeping that spans pause/resume/restart (Legendary restarts its counters every run).</summary>
    public ProgressBaseline Baseline { get; set; } = new();

    /// <summary>Outcome a hook set while the job was finishing (applied with the terminal status). Not persisted.</summary>
    [JsonIgnore]
    public JobOutcome? PendingOutcome { get; set; }

    public PersistedJob Clone() => new()
    {
        Info = Info,
        Request = Request,
        Step = Step,
        CreatedGameFolder = CreatedGameFolder,
        GameFolder = GameFolder,
        AutoRepairAttempted = AutoRepairAttempted,
        VerifyChecked = VerifyChecked,
        VerifyBad = VerifyBad,
        Held = Held,
        Baseline = Baseline.Clone(),
    };

    /// <summary>Jobs of one game and its DLC run in queue order and a paused one blocks the rest of that group only.</summary>
    public string Group => Request.Options?.BaseAppName ?? Request.AppName;
}

internal sealed class ProgressBaseline
{
    /// <summary>Compressed bytes the first download run of the current step reported as "Download size".</summary>
    public long? TotalDownloadBytes { get; set; }

    /// <summary>Bytes the first run had to write (install size minus unchanged), for the written-bytes bar.</summary>
    public long? TotalWriteBytes { get; set; }

    /// <summary>Highest step percent shown so far for the current measuring kind (keeps the phase bar monotonic).</summary>
    public double Percent { get; set; }

    /// <summary>Highest whole-job percent shown so far (monotonic across steps and phases).</summary>
    public double JobPercent { get; set; }

    /// <summary>Active seconds accumulated by earlier runs.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>Active seconds of the current step (for average speeds).</summary>
    public double StepElapsedSeconds { get; set; }

    /// <summary>Phase whose percent <see cref="Percent"/> measures (verify and download percentages are separate bars).</summary>
    public JobPhase? Phase { get; set; }

    public double PeakNetworkBytesPerSecond { get; set; }

    public double PeakDiskBytesPerSecond { get; set; }

    public ProgressBaseline Clone() => (ProgressBaseline)MemberwiseClone();
}
