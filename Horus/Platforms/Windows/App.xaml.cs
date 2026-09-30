using System.Runtime.InteropServices;
using Horus.Application;
using Horus.Application.Diagnostics;
using Horus.Domain.Models;
using Horus.Platforms.Windows;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Horus.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        // PerMonitorV2 keeps text crisp on scaled displays. The unpackaged apphost does
        // not always pick up the manifest's dpiAwareness, so set it here before any HWND.
        private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(nint value);

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { /* already set by the manifest — harmless */ }

            InstallDiagnostics();

            this.InitializeComponent();
        }

        /// <summary>
        /// Turns on crash recording. <b>Windows had none at all</b> — CrashHandler.Install was
        /// called from Platforms/Android/MainApplication and nowhere else, so on this platform
        /// AppDomain.UnhandledException and TaskScheduler.UnobservedTaskException were never
        /// hooked. That is the whole of "it closes after a while and there is nothing in the
        /// logs": there was nothing writing them.
        ///
        /// <para>Here rather than in MauiProgram, and before InitializeComponent, for the same
        /// reason the Android copy is in Application.OnCreate: a failure while the app is
        /// starting is the hardest kind to reproduce and the most likely to be reported as "it
        /// just doesn't open", so the handler has to be in place before there is anything to
        /// fail.</para>
        /// </summary>
        private static void InstallDiagnostics()
        {
            try
            {
                EventLog.Install();
                UserPreferences.ApplyLogLevel();
                CrashHandler.Install();

                // Before anything native is loaded: a Go panic in xray-core and the runtime's own
                // fatal errors are only ever written to stderr, which a GUI process lacks.
                NativeCrashCapture.Install();

                // So the next start knows when this session ended, not just that it did.
                CrashHandler.StartHeartbeat(TimeSpan.FromMinutes(1));

                // WinUI keeps its own unhandled-exception path: an exception on the UI thread
                // is caught by the XAML framework and raised here, and never reaches
                // AppDomain.UnhandledException. Hooking only the AppDomain would have left the
                // most common crash of a desktop app unrecorded.
                Current.UnhandledException += (_, e) =>
                    CrashHandler.Capture("WinUI", e.Exception, terminating: true);

                Diag.Info("app", $"process start, version {AppConfiguration.AppVersion}");

                ReportPreviousSession();

                NativeCrashCapture.RestoreErrorReportingAfterCoreLoads();
            }
            catch
            {
                // Diagnostics setup must never be the thing that stops the app starting.
            }
        }

        /// <summary>
        /// Says how the previous session ended, from everything it left behind: a managed
        /// crash record, its own stderr, the marker's heartbeat, and — in the background —
        /// the Windows event log.
        /// </summary>
        private static void ReportPreviousSession()
        {
            var abrupt = CrashHandler.PreviousSessionEndedAbruptly;
            var native = abrupt ? NativeCrashCapture.ReadPrevious() : null;

            var (crashed, at, summary) = CrashHandler.LastCrash();

            // crash.log is kept until the user acknowledges it in Settings, so its last record
            // can be sessions old. Only one written after the previous session started says
            // anything about how that session ended.
            var recorded = crashed &&
                           (at is null || CrashHandler.PreviousSessionStartedAt is not { } started || at >= started);

            // The runtime's last words name the crash no handler saw. Recorded like any other
            // crash, so Settings shows it — unless a managed handler already recorded this one,
            // in which case stderr only repeats it.
            if (native is { Headline: { } headline } && !recorded)
            {
                CrashHandler.RecordNative(native.At, headline, native.Text);
                (crashed, at, summary) = CrashHandler.LastCrash();
                recorded = true;
            }

            if (crashed)
                Diag.Warn("app", $"previous session ended in a crash at {at:dd.MM HH:mm}", summary);

            if (!abrupt) return;

            // No managed exception and no clean exit. A native fault, a Windows shutdown or
            // sign-out, and a kill from Task Manager all look like this from inside; the
            // inspection below asks Windows which it was.
            if (!recorded)
            {
                var lastAlive = CrashHandler.PreviousSessionLastAlive is { } alive
                    ? $", last sign of life {alive:dd.MM HH:mm}"
                    : string.Empty;

                // Output without a fatal line is usually noise, but it is the only native trace
                // there is; the head goes into the timeline, the whole file into the archive.
                var excerpt = native?.Text is { } text && text.Length > 2000 ? text[..2000] : native?.Text;

                Diag.Warn("app",
                    "previous session ended without unwinding — no managed exception was raised, " +
                    "which points at native code or at Windows ending the process" + lastAlive,
                    excerpt);
            }

            PreviousSessionInspector.InspectInBackground(
                CrashHandler.PreviousSessionStartedAt,
                CrashHandler.PreviousSessionLastAlive,
                nativeOutputFound: native is { Headline: not null });
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
