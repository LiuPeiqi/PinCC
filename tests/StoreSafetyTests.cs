using System.Diagnostics;
using PinCC.Core;

/// <summary>Real SQLite and filesystem boundaries for recovery, cleanup and cancellable maintenance.</summary>
public static class StoreSafetyTests
{
    public static int Passed { get; private set; }
    public static int Run()
    {
        int passed = 0, failed = 0;
        void Check(bool condition, string label)
        {
            if (condition) passed++;
            else { failed++; Console.WriteLine("FAIL " + label); }
        }
        var home = Path.Combine(Path.GetTempPath(), "pincc-store-safety-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new Store(home);
            using var other = new Store(home, busyMs: 100);
            var original = store.Insert(store.Begin(), new Card { Body = "before", Summary = "keep", Opacity = .6 });
            var draft = original.Clone();
            draft.Body = "unsaved editor\n- [ ] quick-add draft";
            var saved = store.SaveRecovery("edit", draft, original.Rev);
            Check(saved.Body == draft.Body && saved.Summary == "keep" && saved.Opacity == .6, "emergency save changes only body");
            var retry = other.SaveRecovery("edit", draft, original.Rev);
            Check(retry.Id == saved.Id && retry.Rev == saved.Rev, "recovery retry does not repeat committed edit");
            var fresh = new Card { Id = -1, Body = "new unsaved card" };
            var newCard = store.SaveRecovery("new", fresh, null);
            var duplicate = other.SaveRecovery("new", fresh, null);
            Check(duplicate.Id == newCard.Id && store.All().Count == 2, "recovery retry never duplicates new card");
            var conflict = saved.Clone(); conflict.Body = "old draft";
            other.Mutate(other.Begin(), saved.Id, c => c.Body = "concurrent change", "edit");
            bool rejected = false;
            try { store.SaveRecovery("conflict", conflict, saved.Rev); } catch (RevConflictException) { rejected = true; }
            Check(rejected && store.Get(saved.Id)!.Body == "concurrent change" && store.Setting("recovery.conflict") == null,
                "recovery conflict keeps newer text and leaves draft uncommitted");
            store.Db.Script("CREATE TEMP TRIGGER fail_recovery BEFORE INSERT ON settings WHEN NEW.key='recovery.rollback' BEGIN SELECT RAISE(ABORT,'recover fail'); END;");
            bool rolledBack = false;
            try { store.SaveRecovery("rollback", new Card { Body = "must not exist" }, null); } catch (InvalidOperationException) { rolledBack = true; }
            store.Db.Script("DROP TRIGGER fail_recovery;");
            Check(rolledBack && store.All().All(c => c.Body != "must not exist"), "recovery marker and card roll back together");

            var sessionCard = store.Insert(store.Begin(), new Card { Body = "session before", Summary = "original summary" });
            var session = store.Begin();
            store.Mutate(session, sessionCard.Id, c => c.Body = "autosaved", "edit");
            other.Mutate(other.Begin(), sessionCard.Id, c => c.Summary = "external summary", "summary");
            var pending = store.Get(sessionCard.Id)!; pending.Body = "pending at shutdown";
            store.SaveRecovery("session", pending, pending.Rev, session.Number);
            store.Restore(session.Number);
            var undone = store.Get(sessionCard.Id)!;
            Check(undone.Body == "session before" && undone.Summary == "external summary",
                "emergency draft and prior autosave share one undo while preserving external columns");
            var unrelated = store.Insert(store.Begin(), new Card { Body = "unrelated before" });
            var unrelatedDraft = unrelated.Clone(); unrelatedDraft.Body = "unrelated recovered";
            store.SaveRecovery("unrelated", unrelatedDraft, unrelated.Rev, session.Number);
            store.Restore(session.Number);
            Check(store.Get(unrelated.Id)!.Body == "unrelated recovered", "unrelated draft cannot join another card's edit session");

            var initialDraft = new Card { Body = "not yet autosaved" };
            var firstRecovery = store.SaveRecovery("initial-session", initialDraft, null, null, out var firstGroup);
            var retriedRecovery = other.SaveRecovery("initial-session", initialDraft, null, null, out var retryGroup);
            Check(firstGroup > 0 && retryGroup == firstGroup && firstRecovery.Id == retriedRecovery.Id,
                "first checkpoint returns its undo group and retries preserve the same group");
            var continuedSession = new Group { Id = firstGroup };
            store.Mutate(continuedSession, firstRecovery.Id, c => c.Body = "continued after cancelled logoff", "edit");
            store.Restore(firstGroup);
            Check(store.Get(firstRecovery.Id)!.DeletedAt != null, "continued edit and first checkpoint undo as one new card gesture");

            var batchGroup = store.Begin();
            store.Db.Script("CREATE TEMP TRIGGER fail_batch BEFORE INSERT ON cards WHEN NEW.body='fail second' BEGIN SELECT RAISE(ABORT,'batch fail'); END;");
            bool batchFailed = false;
            try { store.InsertMany(batchGroup, [new Card { Body = "first batch card" }, new Card { Body = "fail second" }]); }
            catch (InvalidOperationException) { batchFailed = true; }
            store.Db.Script("DROP TRIGGER fail_batch;");
            Check(batchFailed && store.All().All(c => c.Body != "first batch card") && batchGroup.Empty && batchGroup.Cards.Count == 0,
                "multi-card failure rolls back cards and undo bookkeeping together");
            var batchCount = store.All().Count;
            var skipped = store.InsertMany(store.Begin(), [new Card { Body = "stale result" }], () => false);
            Check(skipped.Count == 0 && store.All().Count == batchCount, "stale source check runs before any batch insert");

            var orphan = store.SaveImage([1, 2, 3]);
            File.SetLastWriteTimeUtc(store.ResolveImage(orphan), DateTime.UtcNow.AddDays(-40));
            other.SaveImage([1, 2, 3]); // reused content, before its card insert commits
            store.Purge(30);
            Check(File.Exists(store.ResolveImage(orphan)), "new image lease survives maintenance before card commit");
            File.SetLastWriteTimeUtc(store.ResolveImage(orphan), DateTime.UtcNow.AddDays(-40));
            var referenced = store.SaveImage([4, 5, 6]);
            store.Insert(store.Begin(), new Card { Body = "![image](" + referenced.ToUpperInvariant() + ")" });
            File.SetLastWriteTimeUtc(store.ResolveImage(referenced), DateTime.UtcNow.AddDays(-40));
            store.Purge(30);
            Check(!File.Exists(store.ResolveImage(orphan)) && File.Exists(store.ResolveImage(referenced)),
                "old garbage removed while case-insensitive markdown references survive");

            using var ready = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var held = Task.Run(() =>
            {
                using var lease = HomeLease.TryEnter(home, "Images", 2000) ?? throw new Exception("test lease unavailable");
                ready.Set(); release.Wait(TimeSpan.FromSeconds(5));
            });
            Check(ready.Wait(TimeSpan.FromSeconds(2)), "maintenance lock acquired by competing thread");
            var timer = Stopwatch.StartNew();
            bool busy = false;
            try { store.Mutate(store.Begin(), saved.Id, c => c.Body = "blocked write", "edit"); }
            catch (MaintenanceBusyException) { busy = true; }
            finally { release.Set(); held.GetAwaiter().GetResult(); }
            Check(busy && timer.Elapsed < TimeSpan.FromSeconds(2) && store.Get(saved.Id)!.Body == "concurrent change",
                "slow maintenance does not block a writer indefinitely or partially commit");

            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            timer.Restart();
            bool cancelled = false;
            try { other.Db.Script("WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n;", cancel.Token, TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && timer.Elapsed < TimeSpan.FromSeconds(3), "SQLite maintenance cancellation interrupts actual query");
            Check(Convert.ToInt64(other.Db.Scalar("SELECT 42")) == 42, "connection usable after cancellation");
            bool timedOut = false;
            try { other.Db.Script("WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n;", default, TimeSpan.FromMilliseconds(30)); }
            catch (TimeoutException) { timedOut = true; }
            Check(timedOut, "SQLite maintenance deadline classified as timeout");
            using var stopped = new CancellationTokenSource(); stopped.Cancel();
            cancelled = false;
            try { store.Backup(stopped.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !Directory.Exists(Path.Combine(home, "backup")), "cancelled backup creates no partial output");
            Directory.CreateDirectory(Path.Combine(home, "backup"));
            var staleBackup = Path.Combine(home, "backup", "pincc-20260901.db." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(staleBackup, "interrupted backup");
            using var lockReady = new ManualResetEventSlim();
            using var unlock = new ManualResetEventSlim();
            store.Db.Exec("UPDATE ops SET at=?", When.NowMs - 40L * 86400000);
            var historyBefore = store.Ops(1000).Count;
            var writer = Task.Run(() => store.Db.Tx(() => { lockReady.Set(); unlock.Wait(TimeSpan.FromSeconds(5)); }));
            Check(lockReady.Wait(TimeSpan.FromSeconds(2)), "competing SQLite writer acquired");
            using var purgeCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            timer.Restart();
            cancelled = false;
            try { other.Purge(30, purgeCancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            finally { unlock.Set(); writer.GetAwaiter().GetResult(); }
            Check(cancelled && timer.Elapsed < TimeSpan.FromSeconds(2) && store.Ops(1000).Count == historyBefore,
                "maintenance cancellation under write contention leaves history intact");
            store.Backup();
            Check(!File.Exists(staleBackup), "orphaned own backup intermediate removed under maintenance lease");
            store.Backup();
            var backups = Directory.GetFiles(Path.Combine(home, "backup"));
            Check(backups.Length == 1 && !Directory.GetFiles(Path.Combine(home, "backup"), "*.tmp").Any(), "daily backup publishes one copy without intermediates");
            using var backup = new Db(backups.Single());
            Check((string?)backup.Scalar("PRAGMA quick_check") == "ok", "published backup is valid SQLite");
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, true); }
        Passed = passed;
        Console.WriteLine($"StoreSafetyTests: {passed} passed, {failed} failed");
        return failed;
    }
}
