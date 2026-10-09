using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using PinCC.Core;

namespace PinCC.UI;

/// <summary>No main window: the desk (cards + hubs + tray) is the app.</summary>
public sealed class App : Application
{
    Desk? _desk;
    EventWaitHandle? _show;
    RegisteredWaitHandle? _wait;
    bool _exiting, _prepared;
    int _faultActive, _handlerCount, _shutdownCount, _exitCode, _draftCount, _savedCount;
    string? _recoveryPath;
    string[] _draftBodies = [];
    List<EditDraft> _pendingDrafts = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Install the boundary before resources, native hooks or the first window are created.
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true; // We are shutting down; never resume the failing operation.
            _handlerCount++;
            HandleFatal(ex.Exception, "dispatcher");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log(ex.ExceptionObject as Exception, "app-domain");
        TaskScheduler.UnobservedTaskException += (_, ex) => Log(ex.Exception, "unobserved-task");

        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag)));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PinCC;component/UI/Theme.xaml") });
        if (Environment.GetEnvironmentVariable("PINCC_UI_SNAPSHOT") is { Length: > 0 } ui)
        {
            Palette.ApplyChrome(Resources);
            _desk = new Desk(new Store(busyMs: 2000));
            _desk.LoadSettings();
            _desk.SnapshotUi(ui);
            RequestExit();
            return;
        }

        // Directory scans and recovery are finished before UI reads the initial immutable caches.
        await Task.Run(() =>
        {
            DraftRecovery.Recover(Store.DefaultHome);
            Skills.Refresh(Store.DefaultHome);
            Runners.Refresh(Store.DefaultHome);
        });
        if (_exiting) return;
        _desk = new Desk(new Store(busyMs: 2000));
        _desk.Start(startup: e.Args.Contains("--startup"));
        SessionEnding += (_, _) => _desk.Flush();

        if (Program.SmokeScenario is { } scenario)
        {
            _ = Dispatcher.BeginInvoke(() => RunSmoke(scenario));
            return;
        }
        if (Environment.GetEnvironmentVariable("PINCC_SNAPSHOT") is { Length: > 0 } snap)
        {
            var delay = int.TryParse(Environment.GetEnvironmentVariable("PINCC_SNAPSHOT_DELAY"), out var d) ? d : 4;
            var steps = (Environment.GetEnvironmentVariable("PINCC_STEPS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int next = 0;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            t.Tick += (_, _) =>
            {
                if (_exiting) { t.Stop(); return; }
                t.Interval = TimeSpan.FromSeconds(1.5);
                _desk.Snapshot(next == 0 ? snap : Path.Combine(snap, $"{next:00}-{string.Concat(steps[next - 1].Split(Path.GetInvalidFileNameChars()))}"));
                if (next == steps.Length) { t.Stop(); RequestExit(); return; }
                try { _desk.Step(steps[next]); } catch (Exception error) { Log(error, "snapshot-step"); }
                next++;
            };
            t.Start();
        }

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) =>
        {
            if (!_exiting && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() => { if (!_exiting) _desk.ShowAll(); });
        }, null, -1, false);
    }

    internal void HandleFatal(Exception error, string operation)
    {
        if (Interlocked.Exchange(ref _faultActive, 1) != 0)
        {
            // Nothing on this path creates or measures WPF content, including error notifications.
            _exitCode = 1;
            if (_exiting) return;
        }
        Log(error, operation);
        try { RequestExit(1); }
        catch (Exception failure) { Log(failure, "fatal-exit"); Environment.Exit(1); }
    }

    public async void RequestExit(int exitCode = 0)
    {
        if (_exiting) { _exitCode = Math.Max(_exitCode, exitCode); return; }
        _exiting = true;
        _exitCode = exitCode;
        try
        {
            var drafts = _pendingDrafts = _desk?.PrepareShutdown() ?? [];
            _draftCount = drafts.Count;
            _draftBodies = drafts.Select(d => d.Card.Body).ToArray();
            var home = _desk?.Store.Home ?? Store.DefaultHome;
            // The thread that owns the Recovery lease writes the checkpoint and then saves/deletes under the same lease.
            var checkpoint = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var saving = Task.Run(() =>
            {
                try
                {
                    _savedCount = DraftRecovery.SaveCheckpoint(home, drafts, path =>
                    {
                        _recoveryPath = path;
                        checkpoint.TrySetResult(path);
                    }).Count;
                }
                catch (Exception error) { Log(error, "draft-save"); }
                finally { checkpoint.TrySetResult(_recoveryPath); }
            });
            _recoveryPath = await checkpoint.Task; // Do not exit before a durable checkpoint or an explicit write failure.
            _prepared = drafts.Count == 0 || _recoveryPath != null;
            try { await saving.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException error)
            {
                Log(error, "draft-save-timeout");
                // The worker owns its connection. Observe completion without touching UI or a disposed Store.
                _ = saving.ContinueWith(t => { if (t.Exception != null) Log(t.Exception, "draft-save-late"); }, TaskScheduler.Default);
            }
            _prepared = drafts.Count == 0 || _recoveryPath != null || _savedCount == drafts.Count;
        }
        catch (Exception error) { Log(error, "prepare-exit"); }
        finally
        {
            try { Shutdown(_exitCode); }
            catch (Exception error) { Log(error, "shutdown"); Environment.Exit(Math.Max(1, _exitCode)); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCount++;
        _exiting = true;
        if (!_prepared)
        {
            try { _recoveryPath = DraftRecovery.WriteFallback(_desk?.Store.Home ?? Store.DefaultHome, _pendingDrafts.Count > 0 ? _pendingDrafts : _desk?.PrepareShutdown() ?? []); }
            catch (Exception error) { Log(error, "exit-draft"); }
        }
        Cleanup(() => _desk?.Shutdown(), "desk-dispose");
        Cleanup(() => _wait?.Unregister(null), "show-wait-dispose");
        Cleanup(() => _show?.Dispose(), "show-event-dispose");
        Cleanup(() => _desk?.Store.Dispose(), "store-dispose");
        if (Program.SmokeScenario == "exit-fault") Cleanup(() => throw new IOException("UI smoke exit fault"), "smoke-exit-fault");
        if (Program.SmokeScenario is { } scenario)
        {
            Cleanup(() => File.WriteAllText(Path.Combine(Store.DefaultHome, "ui-smoke-result.json"), JsonSerializer.Serialize(new
            {
                scenario, handlerCount = _handlerCount, shutdownCount = _shutdownCount, draftCount = _draftCount,
                savedCount = _savedCount, draftBodies = _draftBodies, exitCode = _exitCode,
                recoveryPending = _savedCount != _draftCount || _recoveryPath != null && File.Exists(_recoveryPath),
            })), "smoke-result");
        }
        base.OnExit(e);
    }

    static void Cleanup(Action action, string operation)
    {
        try { action(); } catch (Exception error) { Log(error, operation); }
    }

    internal static void Log(Exception? error, string operation = "ui")
    {
        // Logging has its own boundary and cannot start another layout or throw into a native callback.
        try
        {
            Directory.CreateDirectory(Store.DefaultHome);
            File.AppendAllText(Path.Combine(Store.DefaultHome, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid={Environment.ProcessId} version={typeof(App).Assembly.GetName().Version} operation={operation} hresult=0x{error?.HResult:X8} {error}\n\n");
        }
        catch { }
    }

    void RunSmoke(string scenario)
    {
        if (_desk == null || _exiting) return;
        _desk.MakeSmokeDrafts();
        switch (scenario)
        {
            case "edit-exit": case "exit-fault": RequestExit(); break;
            case "save-failure": _desk.VerifySmokeSaveFailure(); RequestExit(); break;
            case "notify-fault":
                _desk.Notify("Injected notification");
                break;
            case "layout-fault":
                var window = new Window { Content = new FaultingTextBox(), Width = 220, Height = 100, ShowInTaskbar = false };
                window.Show();
                break;
            default: throw new ArgumentException("Unknown UI smoke scenario: " + scenario);
        }
    }

    sealed class FaultingTextBox : System.Windows.Controls.TextBox
    {
        protected override Size MeasureOverride(Size constraint) =>
            throw new UnauthorizedAccessException("UI smoke layout fault");
    }
}
