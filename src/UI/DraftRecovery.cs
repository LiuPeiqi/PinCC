using System.Text.Json;
using PinCC.Core;

namespace PinCC.UI;

/// <summary>Plain data, captured before shutdown or rendering callbacks can discard an input.</summary>
internal sealed record EditDraft(string Key, Card Card, long? ExpectedRev, long Ref = 0, string? EditorText = null, string? AddText = null, long? GroupNumber = null);

internal sealed record RecoveredDraft(Card Card, long GroupNumber);

internal static class DraftRecovery
{
    public static string? Write(string home, IReadOnlyList<EditDraft> drafts)
    {
        if (drafts.Count == 0) return null;
        return WriteAt(Path.Combine(home, "recovery"), drafts);
    }

    static string FallbackDirectory(string home) => Program.IsDiagnostic ? Path.Combine(home, "recovery-local") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinCC", "recovery-fallback",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant())))[..16]);

    public static string? WriteFallback(string home, IReadOnlyList<EditDraft> drafts) =>
        drafts.Count == 0 ? null : WriteAt(FallbackDirectory(home), drafts);

    static string WriteAt(string dir, IReadOnlyList<EditDraft> drafts)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"edits-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, drafts);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    public static Dictionary<string, RecoveredDraft> SaveCheckpoint(string home, IReadOnlyList<EditDraft> drafts, Action<string?> checkpoint)
    {
        using var lease = HomeLease.TryEnter(home, "Recovery", 250);
        string? path = null;
        try { path = WriteFallback(home, drafts); }
        catch (Exception error)
        {
            App.Log(error, "draft-local-write");
            try { path = Write(home, drafts); } catch (Exception failure) { App.Log(failure, "draft-write"); }
        }
        checkpoint(path);
        if (lease == null) return []; // Keep the durable file for a later coordinated recovery; do not clear markers.
        var saved = Save(home, drafts);
        if (saved.Count == drafts.Count)
        {
            try { Complete(home, path, drafts); } catch (Exception error) { App.Log(error, "draft-cleanup"); }
        }
        return saved;
    }

    static Dictionary<string, RecoveredDraft> Save(string home, IReadOnlyList<EditDraft> drafts)
    {
        var saved = new Dictionary<string, RecoveredDraft>();
        if (drafts.Count == 0) return saved;
        using var store = new Store(home, busyMs: 250);
        foreach (var draft in drafts)
        {
            try
            {
                var card = store.SaveRecovery(draft.Key, draft.Card, draft.ExpectedRev, draft.GroupNumber, out var group);
                saved[draft.Key] = new RecoveredDraft(card, group);
            }
            catch (Exception error) { App.Log(error, "draft-save"); }
        }
        return saved;
    }

    public static void Complete(string home, string? path, IReadOnlyList<EditDraft> drafts)
    {
        // Delete first: a file whose removal failed still needs its exactly-once markers.
        if (path != null) File.Delete(path);
        if (drafts.Count == 0) return;
        using var store = new Store(home, busyMs: 250);
        foreach (var draft in drafts)
        {
            store.SetSetting("recovery." + draft.Key, (string?)null);
            store.SetSetting("recovery-group." + draft.Key, (string?)null);
        }
    }

    // Conflicting rows and failed files remain available for recovery; never overwrite an external edit.
    public static void Recover(string home)
    {
        // The complete read -> save -> delete -> marker cleanup sequence shares one process-independent lease.
        using var lease = HomeLease.TryEnter(home, "Recovery", 250);
        if (lease == null) return;
        var files = new[] { Path.Combine(home, "recovery"), FallbackDirectory(home) }
            .Where(Directory.Exists).SelectMany(dir => Directory.EnumerateFiles(dir, "edits-*.json")).Take(100);
        foreach (var path in files)
        {
            try
            {
                var drafts = JsonSerializer.Deserialize<List<EditDraft>>(File.ReadAllText(path)) ?? [];
                if (Save(home, drafts).Count == drafts.Count) Complete(home, path, drafts);
            }
            catch (Exception error) { App.Log(error, "draft-recovery"); }
        }
    }
}
