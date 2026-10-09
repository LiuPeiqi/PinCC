using System.Diagnostics;

namespace PinCC.Core;

/// <summary>One deadline and identity for source reading, queued work, the model and result application.</summary>
public sealed class RunContext : IDisposable
{
    readonly CancellationToken _caller;
    readonly CancellationTokenSource _stop;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly string _home;
    readonly string? _skill, _runner;
    readonly long? _card;
    int _complete;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public CancellationToken Token => _stop.Token;
    public TimeSpan Budget { get; }
    internal bool HasRunnerSnapshot { get; private set; }
    internal Llm? SelectedLlm { get; private set; }
    internal string? RunnerProblem { get; private set; }
    public TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, (Budget - _clock.Elapsed).Ticks));

    RunContext(Store store, TimeSpan budget, CancellationToken ct, string? skill, string? runner, long? card)
    {
        _home = store.Home; _skill = skill; _runner = runner; _card = card; _caller = ct;
        Budget = budget;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop.CancelAfter(budget);
        Write("run", "start", detail: new { budget_ms = budget.TotalMilliseconds });
    }

    public static RunContext Start(Store store, Skill skill, Llm llm, CancellationToken ct = default, long? card = null)
    {
        // Existing long context steps remain valid. A shared explicit setting may impose a shorter whole-task budget.
        var selected = skill.Runner != null && skill.Runner != llm.Tool ? Llm.FromSettings(store, skill.Runner) : llm;
        double seconds = (skill.Reads("source") ? 85 : 0) + skill.Context.Where(s => s.Run != null).Sum(s => (double)s.Timeout)
            + 60 + ((selected ?? llm).IsWsl ? 180 : 150) + 15;
        var run = Start(store, TimeSpan.FromSeconds(Math.Min(seconds, 86400)), ct, skill.Name, skill.Runner ?? llm.Tool, card);
        run.HasRunnerSnapshot = true;
        run.SelectedLlm = selected;
        run.RunnerProblem = selected == null ? Llm.Problem(store, skill.Runner) : null;
        return run;
    }

    public static RunContext Start(Store store, TimeSpan budget, CancellationToken ct = default, string? skillName = null,
        string? runner = null, long? card = null)
    {
        if (int.TryParse(store.Setting("llm.run_timeout"), out var seconds) && seconds is > 0 and <= 86400)
            budget = TimeSpan.FromSeconds(seconds);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        return new RunContext(store, budget, ct, skillName, runner, card);
    }

    public void ThrowIfStopped()
    {
        _caller.ThrowIfCancellationRequested();
        if (_stop.IsCancellationRequested || Remaining == TimeSpan.Zero) throw new TimeoutException("整项任务超过时间预算");
    }

    public IDisposable Phase(string phase, object? detail = null)
    {
        ThrowIfStopped();
        return new Stage(this, phase, detail);
    }

    public void Complete(string outcome, string? error = null)
    {
        if (Interlocked.Exchange(ref _complete, 1) == 0) Write("run", "terminal", outcome, error);
    }

    void Write(string phase, string kind, string? outcome = null, string? error = null, object? detail = null) =>
        Diagnostics.Write(_home, new { run_id = Id, skill = _skill, card = _card, runner = _runner, phase, @event = kind,
            elapsed_ms = _clock.ElapsedMilliseconds, outcome, error, detail });

    public void Dispose()
    {
        Complete(_caller.IsCancellationRequested ? "cancelled" : _stop.IsCancellationRequested ? "timeout" : "abandoned");
        _stop.Dispose();
    }

    sealed class Stage : IDisposable
    {
        readonly RunContext _run;
        readonly string _phase;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        int _disposed;
        public Stage(RunContext run, string phase, object? detail) { _run = run; _phase = phase; run.Write(phase, "start", detail: detail); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _run.Write(_phase, "end", detail: new { phase_ms = _clock.ElapsedMilliseconds });
        }
    }
}
