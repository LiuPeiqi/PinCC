using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PinCC.Core;

/// <summary>
/// Runs a child process with stdin → EOF, stdout/stderr drained concurrently as raw bytes, one deadline for the whole
/// run, and a Job object that kills the whole child tree if PinCC exits or crashes (PinCC itself is never in the job).
/// </summary>
public static class Proc
{
    public sealed record Result(int Code, string Out, string Err);

    static readonly Lazy<IntPtr> Job = new(CreateJob);

    /// <param name="rawArgs">Exact command line (cmd.exe needs its own quoting); otherwise <paramref name="args"/> are quoted per token.</param>
    /// <param name="maxOutput">Bytes of stdout kept; the rest is drained and dropped so the child never blocks on a full pipe.</param>
    public static async Task<Result> Run(string file, IEnumerable<string> args, string? stdin, TimeSpan timeout,
        CancellationToken ct = default, IDictionary<string, string>? env = null, string? cwd = null, string? rawArgs = null,
        int maxOutput = 8_000_000)
    {
        // The deadline starts before any I/O: a child that never reads stdin must not hang the stdin write.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Path.GetTempPath(),
        };
        if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false); // Encoding.UTF8 would send a BOM
        if (rawArgs != null) psi.Arguments = rawArgs;
        else foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + file);
        try { AssignProcessToJobObject(Job.Value, p.Handle); } catch { /* job unavailable: the deadline still applies */ }
        var output = new Sink(p.StandardOutput.BaseStream, maxOutput);
        var error = new Sink(p.StandardError.BaseStream, 1_000_000);
        if (stdin != null)
        {
            var (input, token) = (p.StandardInput, cts.Token); // taken now: both may be disposed before the writer runs
            _ = Task.Run(async () =>
            {
                // Disposing sends EOF (claude / codex / ollama wait for it). The write runs off the awaited path, so a
                // child that never reads cannot stall us: the deadline kills it and the blocked write fails.
                try { await using (input) await input.WriteAsync(stdin.AsMemory(), token); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
            }, CancellationToken.None);
        }
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"超过 {timeout.TotalSeconds:0} 秒未完成");
        }
        // A grandchild that inherited the pipes can hold them open after the child exits: take what arrived.
        await Task.WhenAny(Task.WhenAll(output.Done, error.Done), Task.Delay(3000, CancellationToken.None)).ConfigureAwait(false);
        return new Result(p.ExitCode, output.Text(), error.Text());
    }

    /// <summary>Drains a pipe into memory (up to a cap); decoded like files: BOM → strict UTF-8 → GB18030 (cmd.exe speaks GBK).</summary>
    sealed class Sink
    {
        readonly MemoryStream _ms = new();
        bool _truncated;
        public Task Done { get; }

        public Sink(Stream s, int max) => Done = Task.Run(async () =>
        {
            var buf = new byte[65536];
            try
            {
                for (int n; (n = await s.ReadAsync(buf).ConfigureAwait(false)) > 0;)
                    lock (_ms)
                    {
                        var keep = (int)Math.Clamp(max - _ms.Length, 0, n);
                        _ms.Write(buf, 0, keep);
                        _truncated |= keep < n;
                    }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        });

        public string Text()
        {
            lock (_ms) return Fetch.Text(_ms.ToArray(), _truncated);
        }
    }

    static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, 9, ptr, (uint)size); // JobObjectExtendedLimitInformation
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job; // intentionally never closed: the handle closes when PinCC exits, killing the children
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
