using System.Globalization;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // WPF defaults to en-US; the UI culture picks CJK line breaking and font fallback.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag)));
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PinCC;component/UI/Theme.xaml") });
        if (Environment.GetEnvironmentVariable("PINCC_UI_SNAPSHOT") is { Length: > 0 } ui)
        {
            // Diagnostic: builds the auxiliary windows and renders them off-screen, then exits. Nothing is shown.
            Palette.ApplyChrome(Resources);
            var d = new Desk(new Store(busyMs: 2000));
            d.LoadSettings();
            d.SnapshotUi(ui);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception);
            ex.Handled = true;
            _desk?.Notify("出错了（已记录到 error.log）：" + ex.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log(ex.ExceptionObject as Exception);

        _desk = new Desk(new Store(busyMs: 2000));
        _desk.Start(startup: e.Args.Contains("--startup"));
        SessionEnding += (_, _) => _desk.Flush(); // save open edits; the logoff may still be cancelled, so keep running

        if (Environment.GetEnvironmentVariable("PINCC_SNAPSHOT") is { Length: > 0 } snap)
        {
            // A diagnostic run: snapshot after the delay, then after each scripted step (PINCC_STEPS="done:3;undo"), then exit.
            var delay = int.TryParse(Environment.GetEnvironmentVariable("PINCC_SNAPSHOT_DELAY"), out var d) ? d : 4;
            var steps = (Environment.GetEnvironmentVariable("PINCC_STEPS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int next = 0;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            t.Tick += (_, _) =>
            {
                t.Interval = TimeSpan.FromSeconds(1.5);
                _desk.Snapshot(next == 0 ? snap : Path.Combine(snap, $"{next:00}-{string.Concat(steps[next - 1].Split(Path.GetInvalidFileNameChars()))}"));
                if (next == steps.Length) { t.Stop(); Shutdown(); return; }
                try { _desk.Step(steps[next]); } catch (Exception e) { Log(e); }
                next++;
            };
            t.Start();
        }

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Dispatcher.BeginInvoke(() => _desk.ShowAll()), null, -1, false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _desk?.Shutdown();
        _wait?.Unregister(null);
        _show?.Dispose();
        _desk?.Store.Dispose();
        base.OnExit(e);
    }

    static void Log(Exception? ex)
    {
        try { File.AppendAllText(Path.Combine(Store.DefaultHome, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n"); } catch { }
    }
}
