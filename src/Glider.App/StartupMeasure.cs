using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Glider.App.Native;

namespace Glider.App;

/// <summary>
/// <c>--measure-startup[=file] [--measure-delay=N]</c>: waits for the window to be revealed, lets the app settle N seconds, writes
/// milestone timings and memory (host + every WebView2 descendant process) to a JSON file and exits.
/// "privateWorkingSetMb" counts only non-shared resident pages (what Task Manager shows as "Memory"); "privateBytesMb" is commit.
/// </summary>
internal static class StartupMeasure
{
    public static void Begin(HostRuntime rt, MainWindow window, string file, int settleSeconds) =>
        _ = Task.Run(async () =>
        {
            try
            {
                var waited = 0;
                while (!StartupTimeline.Has("revealed") && waited < 20_000)
                {
                    await Task.Delay(50);
                    waited += 50;
                }

                await Task.Delay(TimeSpan.FromSeconds(settleSeconds));
                Write(file, settleSeconds);
            }
            catch (Exception ex)
            {
                HostLog.Error("measure failed", ex);
            }
            finally
            {
                _ = rt.Ui.BeginInvoke(window.QuitNow);
            }
        });

    /// <summary>(host, webview2 processes) private working set in MB right now; used by the self-test to compare states.</summary>
    public static (double HostMb, double WebViewMb) PrivateWorkingSetTotalsMb()
    {
        using var self = Process.GetCurrentProcess();
        var host = Sample((uint)self.Id, self.ProcessName)?.PrivateWorkingSetMb ?? 0;
        var web = Descendants((uint)self.Id).Select(p => Sample(p.Pid, p.Name)?.PrivateWorkingSetMb ?? 0).Sum();
        return (host, Math.Round(web, 1));
    }

    private static void Write(string file, int settleSeconds)
    {
        using var self = Process.GetCurrentProcess();
        var host = Sample((uint)self.Id, self.ProcessName);
        var children = Descendants((uint)self.Id).Select(p => Sample(p.Pid, p.Name)).Where(s => s is not null).Select(s => s!).ToList();

        using var stream = File.Create(file);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("timestamp", DateTimeOffset.Now);
#if DEBUG
        w.WriteString("configuration", "Debug");
#else
        w.WriteString("configuration", "Release");
#endif
        w.WriteString("runtime", RuntimeInformation.FrameworkDescription);
        w.WriteBoolean("mica", HostEnvironment.ActiveBackdrop == Glider.Core.Contracts.BackdropKind.Mica);
        w.WriteNumber("settleSeconds", settleSeconds);
        w.WriteStartObject("marksMs");
        foreach (var m in StartupTimeline.Snapshot())
        {
            w.WriteNumber(m.Key, m.Value);
        }

        w.WriteEndObject();
        if (host is not null)
        {
            w.WritePropertyName("host");
            host.Write(w);
        }

        w.WriteStartArray("webview2Processes");
        foreach (var c in children)
        {
            c.Write(w);
        }

        w.WriteEndArray();
        w.WriteStartObject("totalsMb");
        w.WriteNumber("hostPrivateWorkingSet", host?.PrivateWorkingSetMb ?? 0);
        w.WriteNumber("webview2PrivateWorkingSet", Math.Round(children.Sum(c => c.PrivateWorkingSetMb), 1));
        w.WriteNumber("allPrivateWorkingSet", Math.Round((host?.PrivateWorkingSetMb ?? 0) + children.Sum(c => c.PrivateWorkingSetMb), 1));
        w.WriteNumber("allPrivateBytes", Math.Round((host?.PrivateBytesMb ?? 0) + children.Sum(c => c.PrivateBytesMb), 1));
        w.WriteNumber("allWorkingSet", Math.Round((host?.WorkingSetMb ?? 0) + children.Sum(c => c.WorkingSetMb), 1));
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private sealed record ProcSample(uint Pid, string Name, double WorkingSetMb, double PrivateBytesMb, double PrivateWorkingSetMb)
    {
        public void Write(Utf8JsonWriter w)
        {
            w.WriteStartObject();
            w.WriteNumber("pid", Pid);
            w.WriteString("name", Name);
            w.WriteNumber("workingSetMb", WorkingSetMb);
            w.WriteNumber("privateBytesMb", PrivateBytesMb);
            w.WriteNumber("privateWorkingSetMb", PrivateWorkingSetMb);
            w.WriteEndObject();
        }
    }

    private static ProcSample? Sample(uint pid, string name)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_INFORMATION | Win32.PROCESS_VM_READ, false, pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            var c = new PROCESS_MEMORY_COUNTERS_EX { Cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
            if (!Win32.K32GetProcessMemoryInfo(h, ref c, c.Cb))
            {
                return null;
            }

            const double Mb = 1024.0 * 1024.0;
            return new ProcSample(pid, name, Math.Round(c.WorkingSetSize / Mb, 1), Math.Round(c.PrivateUsage / Mb, 1), Math.Round(PrivateWorkingSetBytes(h) / Mb, 1));
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    /// <summary>Resident pages that are not shared with another process.</summary>
    private static unsafe long PrivateWorkingSetBytes(nint process)
    {
        uint size = 1 << 20;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((nint)size);
            try
            {
                if (Win32.K32QueryWorkingSet(process, buffer, size))
                {
                    var count = *(nuint*)buffer;
                    var entries = (nuint*)buffer + 1;
                    long priv = 0;
                    for (nuint i = 0; i < count; i++)
                    {
                        if ((entries[i] & 0x100) == 0) // PSAPI_WORKING_SET_BLOCK.Shared
                        {
                            priv++;
                        }
                    }

                    return priv * 4096;
                }

                size *= 4;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return 0;
    }

    private static unsafe List<(uint Pid, string Name)> Descendants(uint root)
    {
        var all = new List<(uint Pid, uint Parent, string Name)>();
        var snap = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snap is 0 or -1)
        {
            return [];
        }

        try
        {
            var e = new PROCESSENTRY32W { Size = (uint)sizeof(PROCESSENTRY32W) };
            for (var ok = Win32.Process32First(snap, ref e); ok; ok = Win32.Process32Next(snap, ref e))
            {
                all.Add((e.ProcessId, e.ParentProcessId, new string(e.ExeFile)));
            }
        }
        finally
        {
            Win32.CloseHandle(snap);
        }

        var result = new List<(uint, string)>();
        var frontier = new Queue<uint>([root]);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var p in all.Where(p => p.Parent == parent && p.Pid != parent))
            {
                result.Add((p.Pid, p.Name));
                frontier.Enqueue(p.Pid);
            }
        }

        return result;
    }
}
