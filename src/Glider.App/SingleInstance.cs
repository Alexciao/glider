using System.IO.Pipes;
using System.Security.Principal;
using Glider.App.Native;
using Glider.Core.Links;

namespace Glider.App;

/// <summary>
/// Named mutex = "am I first?". The second instance asks the first to come to the front over a named pipe and exits.
/// The pipe name includes the user SID so sessions/users never collide.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static string _pipeName = "";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    private static string PipeName => _pipeName;

    /// <summary>Returns the owner object when this process is the first instance, otherwise signals the first one and returns null.</summary>
    /// <param name="scope">Empty for the real app; a non-empty scope (derived from --data-dir) isolates test instances from each other and from the user's.</param>
    /// <param name="link">A glider:// (or Epic launcher) link this process was started with; forwarded to the first instance instead of a plain "come to the front".</param>
    public static SingleInstance? TryAcquire(string scope = "", string? link = null)
    {
        _pipeName = "Glider.Activate." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName) + scope;
        var mutex = new Mutex(false, HostConstants.MainMutexName + scope);
        if (TryTake(mutex, TimeSpan.Zero))
        {
            return new SingleInstance(mutex);
        }

        // The owner is starting up, running, or quitting. We were just launched by the user, so we may hand the foreground right to it.
        Win32.AllowSetForegroundWindow(uint.MaxValue);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (TrySignal(link))
            {
                mutex.Dispose();
                return null;
            }

            // Nobody is listening: the owner is still starting up (retry) or is already quitting. If it is quitting it releases the
            // mutex once Core is disposed, and this process then simply becomes the new first instance instead of being swallowed.
            if (TryTake(mutex, TimeSpan.FromMilliseconds(250)))
            {
                return new SingleInstance(mutex);
            }
        }

        if (TryTake(mutex, TimeSpan.FromSeconds(3)))
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    private static bool TryTake(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous owner crashed; ownership is ours now
        }
    }

    private static bool TrySignal(string? link)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1000);
            client.Write(ActivationMessage.Encode(link));
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }
    }

    /// <summary>Stops answering activation requests (idempotent). Called when the dispatcher begins shutting down, so a relaunch is not signalled into a dying process.</summary>
    public void StopListening()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }
    }

    /// <summary>
    /// Starts listening; <paramref name="onActivate"/> runs on a pool thread with the forwarded link, or null for a plain "come to the front".
    /// Malformed or stalled messages are dropped.
    /// </summary>
    public void Listen(Action<string?> onActivate)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var (ok, link) = await ActivationMessage.ReadAsync(server, timeout.Token).ConfigureAwait(false);
                    if (ok)
                    {
                        onActivate(link);
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    HostLog.Warn("Activation message timed out");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    HostLog.Warn("Activation pipe error", ex);
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // released from another thread at process exit: the OS frees it anyway
        }

        _mutex.Dispose();
    }
}
