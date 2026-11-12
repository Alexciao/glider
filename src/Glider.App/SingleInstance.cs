using System.IO.Pipes;
using System.Security.Principal;
using Glider.App.Native;

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
    public static SingleInstance? TryAcquire(string scope = "")
    {
        _pipeName = "Glider.Activate." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName) + scope;
        var mutex = new Mutex(true, HostConstants.MainMutexName + scope, out var first);
        if (first)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        SignalFirst();
        return null;
    }

    private static void SignalFirst()
    {
        // We were just launched by the user, so we may hand the foreground right to the running instance.
        Win32.AllowSetForegroundWindow(uint.MaxValue);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(1000);
                client.WriteByte(1);
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                Thread.Sleep(250); // first instance may still be starting up
            }
        }
    }

    /// <summary>Starts listening; <paramref name="onActivate"/> runs on a pool thread.</summary>
    public void Listen(Action onActivate)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    var buf = new byte[1];
                    _ = await server.ReadAsync(buf, _cts.Token).ConfigureAwait(false);
                    onActivate();
                }
                catch (OperationCanceledException)
                {
                    return;
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
