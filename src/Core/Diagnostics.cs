using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PinCC.Core;

/// <summary>Versioned stage and failure events, separate from the compatible runs.log completion summaries.</summary>
public static class Diagnostics
{
    static readonly string AppVersion = typeof(Diagnostics).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static void Write(string home, object entry)
    {
        try
        {
            var value = JsonSerializer.SerializeToElement(entry, LogFile.Json);
            var record = new Dictionary<string, object?>();
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) record[property.Name] = property.Value;
            else record["detail"] = value;
            record["schema"] = 1;
            record["version"] = AppVersion;
            record["pid"] = Environment.ProcessId;
            record["utc"] = DateTimeOffset.UtcNow;
            LogFile.Write(home, "diagnostics.log", record);
        }
        catch (Exception e) when (LogFile.IsLogFailure(e)) { } // Diagnostics must not interrupt recovery or a model run.
    }
}

/// <summary>One bounded append/rotation transaction for cooperating GUI and CLI processes.</summary>
internal static class LogFile
{
    internal const int MaximumBytes = 1_000_000;
    const int RetainedLines = 500;
    const int LockWaitMilliseconds = 150;
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static bool IsLogFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or JsonException or NotSupportedException or ArgumentException or SecurityException;

    internal static string LockName(string path)
    {
        var canonical = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return "PinCC.Log." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static void Write(string home, string name, object entry)
    {
        string? temporary = null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(home, name));
            var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
            // A pathological event is discarded; no single event can defeat the log size limit.
            if (line.Length > MaximumBytes) return;
            using var mutex = new Mutex(false, LockName(path));
            var entered = false;
            try
            {
                try { entered = mutex.WaitOne(LockWaitMilliseconds); }
                catch (AbandonedMutexException) { entered = true; } // The previous writer died; rotation is still recoverable.
                if (!entered) return;
                Directory.CreateDirectory(home);
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    // A killed writer can leave an incomplete final record. Do not merge the new
                    // event into it; discard only that tail, searching at most the retention budget.
                    if (stream.Length > 0)
                    {
                        stream.Position = stream.Length - 1;
                        if (stream.ReadByte() != '\n')
                        {
                            var count = (int)Math.Min(stream.Length, MaximumBytes);
                            var corruptOffset = stream.Length - count;
                            stream.Position = corruptOffset;
                            var tail = new byte[count];
                            stream.ReadExactly(tail);
                            var boundary = Array.LastIndexOf(tail, (byte)'\n');
                            stream.SetLength(boundary < 0 ? 0 : corruptOffset + boundary + 1);
                        }
                    }
                    stream.Position = stream.Length;
                    stream.Write(line);
                }
                if (new FileInfo(path).Length <= MaximumBytes) return;
                // An older release or an external writer may have left an arbitrarily large log.
                // Read only a bounded tail, including one extra byte to locate a complete record.
                byte[] bytes;
                long offset;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var count = (int)Math.Min(stream.Length, MaximumBytes + 1L);
                    offset = stream.Length - count;
                    stream.Position = offset;
                    bytes = new byte[count];
                    stream.ReadExactly(bytes);
                }
                var start = bytes.Length;
                var retained = 0;
                // Scan complete LF-terminated records from newest to oldest, bounded by both bytes and lines.
                for (var end = bytes.Length; end > 0 && retained < RetainedLines;)
                {
                    var previous = end > 1 ? Array.LastIndexOf(bytes, (byte)'\n', end - 2, end - 1) : -1;
                    var candidate = previous + 1;
                    if ((candidate == 0 && offset > 0) || bytes.Length - candidate > MaximumBytes) break;
                    start = candidate;
                    retained++;
                    end = candidate;
                }
                temporary = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(bytes.AsSpan(start));
                File.Move(temporary, path, overwrite: true);
                temporary = null;
            }
            finally { if (entered) mutex.ReleaseMutex(); }
        }
        catch (Exception e) when (IsLogFailure(e)) { } // A log failure is deliberately isolated from application work.
        finally
        {
            if (temporary != null)
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
