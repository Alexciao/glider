using Glider.Core.Contracts;

namespace Glider.Core.Jobs;

/// <summary>
/// Per-game conditions that keep a writer job from starting (its game runs, EGL is busy with its folder, its cloud saves
/// are syncing). The queue asks <em>before</em> it claims the single writer slot, so a job that has to wait yields the slot
/// to the next eligible job instead of holding it (review H3). Implemented by the service layer's job hooks.
/// </summary>
public interface IJobGate
{
    /// <summary>
    /// Why <paramref name="job"/> can't start now (shown as its blocked reason), or null when it may. Must not block: it is
    /// called on the queue's loop for every waiting job each time the queue re-evaluates.
    /// </summary>
    string? ReasonNotToStart(JobInfo job);
}

/// <summary>
/// Thrown by <see cref="IJobHooks.BeforeRunAsync"/> when a gate closed between the queue's check and the claim: the queue
/// puts the job back as waiting (with <see cref="Reason"/>) and runs the next eligible job. Never a failure.
/// </summary>
public sealed class JobGatedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
