using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PinCC.Core;

/// <summary>Malformed user files must remain isolated, and independent processes must not damage rotating JSONL logs.</summary>
public static class AgentLogTests
{
    const string WorkerFlag = "--agent-log-worker";
    public static int Passed { get; private set; }

    public static bool TryRunChild(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.FirstOrDefault() != WorkerFlag) return false;
        try
        {
            var home = args[1];
            var worker = int.Parse(args[2]);
            var count = int.Parse(args[3]);
            var payload = new string('中', int.Parse(args[4]));
            var gate = args[5];
            var wait = Stopwatch.StartNew();
            while (!File.Exists(gate))
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Log test start gate was not opened.");
                Thread.Sleep(5);
            }
            for (var i = 0; i < count; i++) RunLog.Write(home, new { worker, i, text = payload, ms = i + 100 });
            Diagnostics.Write(home, new { worker, phase = "test", @event = "terminal", outcome = "ok" });
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            exitCode = 1;
        }
        return true;
    }

    public static int Run()
    {
        var failed = 0;
        var passed = 0;
        void Check(bool value, string description)
        {
            if (value) passed++;
            else { failed++; Console.WriteLine("FAIL " + description); }
        }
        var home = Path.Combine(Path.GetTempPath(), "pincc-agent-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            foreach (var (field, path) in new (string Field, string Path)[]
            {
                ("\"args\":null", "$.args"), ("\"args\":[null]", "$.args[0]"),
                ("\"model\":null", "$.model"), ("\"model\":[null]", "$.model[0]"),
                ("\"listModels\":null", "$.listModels"), ("\"listModels\":[\"ls\",null]", "$.listModels[1]"),
                ("\"grants\":null", "$.grants"), ("\"grants\":{\"web\":null}", "$.grants.web"),
                ("\"grants\":{\"none\":[null]}", "$.grants.none[0]"),
                ("\"promptEscapes\":null", "$.promptEscapes"),
                ("\"promptEscapes\":{\"@\":null}", "$.promptEscapes.@"), ("\"name\":null", "$.name"),
            })
            {
                var json = "{\"name\":\"t\",\"command\":\"tool\",\"args\":[]," + field + "}";
                Exception? caught = null;
                try { RunnerProfile.Parse(json, "t", "test"); }
                catch (Exception error) { caught = error; }
                Check(caught is FormatException && caught.Message.Contains(path), "profile structure reports " + path + ": " + caught);
            }
            var compatible = RunnerProfile.Parse("""
                { // Existing permissive JSON format stays supported.
                  "NAME":"t", "command":"tool", "args":null, "ARGS":["go",],
                }
                """, "t", "test");
            Check(compatible.Args.SequenceEqual(["go"]) && compatible.Model == null, "comments, trailing commas, effective case-insensitive property and omitted optional fields");
            var duplicateMaps = RunnerProfile.Parse("""
                {"name":"t","command":"tool","args":["{grants}"],
                 "grants":{"none":null,"none":[]},"promptEscapes":{"@":null,"@":"\\@"}}
                """, "t", "test");
            Check(duplicateMaps.Grants!["none"].Length == 0 && duplicateMaps.PromptEscapes!["@"] == "\\@", "nested duplicate dictionary keys keep the last effective value");

            var directory = Runners.Dir(home);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "claude.json"), """{"name":"claude","args":null}""");
            File.WriteAllText(Path.Combine(directory, "bad.json"), """{"name":"bad","command":"tool","args":[null]}""");
            File.WriteAllText(Path.Combine(directory, "good.json"), """{"name":"good","command":"tool","args":["ask"]}""");
            Runners.Reload(home);
            var snapshot = Runners.All;
            Check(Runners.Errors.Count == 2 && Runners.Errors.Any(e => e.Contains("$.args[0]")), "invalid user files are individually reported");
            Check(Runners.Find("claude")?.Origin == "内置" && Runners.Find("bad") == null && Runners.Find("good") != null, "bad override falls back to built-in, unrelated user profile survives");
            Check(!Runners.Refresh(home), "unchanged malformed files do not trigger reload loops");
            File.WriteAllText(Path.Combine(directory, "good.json"), """{"name":"good","command":"tool","args":["ask-new"]}""");
            Check(Runners.Refresh(home) && Runners.Find("good")!.Args[0] == "ask-new" && snapshot.Single(p => p.Name == "good").Args[0] == "ask", "refresh atomically replaces the cache and preserves existing snapshots");

            var summary = new { skill = "summary", card = 7, ok = true, ms = 1234, error = (string?)null };
            RunLog.Write(home, summary);
            var completion = File.ReadAllText(Path.Combine(home, "runs.log"));
            Check(completion == JsonSerializer.Serialize(summary) + "\n", "runs.log completion fields and ms remain byte-compatible");
            Diagnostics.Write(home, new { run_id = "abc", phase = "source", @event = "start", elapsed_ms = 0 });
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "diagnostics.log"))))
            {
                var record = doc.RootElement;
                Check(record.GetProperty("schema").GetInt32() == 1 && record.GetProperty("pid").GetInt32() == Environment.ProcessId
                    && record.GetProperty("version").GetString()?.Length > 0 && record.GetProperty("utc").TryGetDateTimeOffset(out _)
                    && record.GetProperty("run_id").GetString() == "abc" && record.GetProperty("phase").GetString() == "source", "diagnostic schema, process/version/UTC metadata and caller fields");
            }

            void CheckWriters(string suffix, int count, int textLength, bool rotate)
            {
                var logHome = Path.Combine(home, suffix);
                Directory.CreateDirectory(logHome);
                var path = Path.Combine(logHome, "runs.log");
                if (rotate)
                    for (var i = 0; i < 180; i++) RunLog.Write(logHome, new { seed = i, text = new string('x', 5000) });
                var gate = Path.Combine(logHome, "start");
                var processes = new List<Process>();
                try
                {
                    for (var worker = 0; worker < 4; worker++)
                    {
                        var start = ChildStart(logHome, worker, count, textLength, gate);
                        processes.Add(Process.Start(start) ?? throw new InvalidOperationException("Failed to start test writer."));
                    }
                    File.WriteAllText(gate, "go");
                    foreach (var process in processes)
                    {
                        var exited = process.WaitForExit(30_000);
                        if (!exited) process.Kill(entireProcessTree: true);
                        Check(exited && process.ExitCode == 0, suffix + " child writer completes");
                    }
                    var records = new List<(int Worker, int Index)>();
                    var valid = true;
                    foreach (var line in File.ReadLines(path))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("worker", out var worker))
                                records.Add((worker.GetInt32(), doc.RootElement.GetProperty("i").GetInt32()));
                        }
                        catch (JsonException) { valid = false; }
                    }
                    Check(valid && records.Count == 4 * count && records.Distinct().Count() == 4 * count,
                        suffix + " all concurrent records stay complete and are neither lost nor duplicated");
                    Check(new FileInfo(path).Length <= 1_000_000 && Directory.GetFiles(logHome, "*.tmp").Length == 0,
                        suffix + " rotation respects the byte cap and removes temporary files");
                    var diagnosticPids = File.ReadLines(Path.Combine(logHome, "diagnostics.log")).Select(line =>
                    {
                        using var doc = JsonDocument.Parse(line);
                        return doc.RootElement.GetProperty("pid").GetInt32();
                    }).Distinct().Count();
                    Check(diagnosticPids == 4, suffix + " independent GUI/CLI-style processes all emit diagnostics");
                }
                finally
                {
                    foreach (var process in processes)
                    {
                        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                        process.Dispose();
                    }
                }
            }
            CheckWriters("append", count: 60, textLength: 80, rotate: false);
            CheckWriters("rotate", count: 80, textLength: 600, rotate: true);

            var legacyHome = Path.Combine(home, "legacy-large");
            Directory.CreateDirectory(legacyHome);
            var legacyPath = Path.Combine(legacyHome, "runs.log");
            using (var stream = new FileStream(legacyPath, FileMode.Create, FileAccess.Write))
            {
                var record = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { old = true, text = new string('x', 4000) }) + "\n");
                for (var i = 0; i < 4000; i++) stream.Write(record);
            }
            RunLog.Write(legacyHome, new { sentinel = "newest" });
            var legacyLines = File.ReadAllLines(legacyPath);
            using var legacyNewest = JsonDocument.Parse(legacyLines[^1]);
            Check(new FileInfo(legacyPath).Length <= 1_000_000 && legacyLines.Length <= 500
                && legacyNewest.RootElement.GetProperty("sentinel").GetString() == "newest"
                && legacyLines.All(line => { using var doc = JsonDocument.Parse(line); return doc.RootElement.ValueKind == JsonValueKind.Object; }),
                "oversized logs from older versions are trimmed to complete records while preserving the newest event");
            var boundaryHome = Path.Combine(home, "boundaries");
            Directory.CreateDirectory(boundaryHome);
            var boundaryPath = Path.Combine(boundaryHome, "runs.log");
            File.WriteAllText(boundaryPath, "{\"old\":true}\n{\"incomplete\":");
            RunLog.Write(boundaryHome, new { sentinel = true });
            var recovered = File.ReadAllLines(boundaryPath);
            using (var newest = JsonDocument.Parse(recovered[^1]))
                Check(recovered.Length == 2 && newest.RootElement.GetProperty("sentinel").GetBoolean(), "an abandoned incomplete record cannot corrupt the next append");
            var overhead = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { text = "" }) + "\n");
            File.Delete(boundaryPath);
            RunLog.Write(boundaryHome, new { text = new string('x', 1_000_000 - overhead) });
            Check(new FileInfo(boundaryPath).Length == 1_000_000, "a complete record exactly at the byte cap is accepted");
            RunLog.Write(boundaryHome, new { sentinel = true });
            Check(File.ReadAllLines(boundaryPath).Length == 1, "rotation discards the oversized older record and keeps the newest complete append");
            var beforeOversized = File.ReadAllText(boundaryPath);
            RunLog.Write(boundaryHome, new { text = new string('x', 1_000_000) });
            Check(File.ReadAllText(boundaryPath) == beforeOversized, "a single event above the byte cap is safely dropped");

            // A deliberately held cross-process lock cannot stall the application indefinitely.
            var blockedHome = Path.Combine(home, "blocked");
            Directory.CreateDirectory(blockedHome);
            var blockedPath = Path.Combine(blockedHome, "runs.log");
            using (var mutex = new Mutex(false, LogFile.LockName(blockedPath)))
            {
                mutex.WaitOne();
                try
                {
                    var writer = Task.Run(() => RunLog.Write(blockedHome, new { shouldDrop = true }));
                    Check(writer.Wait(TimeSpan.FromSeconds(2)) && !File.Exists(blockedPath), "busy log mutex has a bounded wait and never breaks application work");
                }
                finally { mutex.ReleaseMutex(); }
            }
            RunLog.Write(blockedHome, summary);
            Check(File.Exists(blockedPath), "writer recovers after lock contention");
            var invalidHome = Path.Combine(home, "not-a-directory");
            File.WriteAllText(invalidHome, "x");
            RunLog.Write(invalidHome, summary);
            Diagnostics.Write(invalidHome, new { phase = "failure" });
            Check(File.ReadAllText(invalidHome) == "x", "unwritable logging destination is isolated");
        }
        finally
        {
            Runners.Reload(Path.Combine(home, "empty"));
            Directory.Delete(home, recursive: true);
        }
        Passed = passed;
        Console.WriteLine($"Agent/log: {passed} passed, {failed} failed");
        return failed;
    }

    static ProcessStartInfo ChildStart(string home, int worker, int count, int payload, string gate)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var value in new[] { WorkerFlag, home, worker.ToString(), count.ToString(), payload.ToString(), gate }) start.ArgumentList.Add(value);
        return start;
    }
}
