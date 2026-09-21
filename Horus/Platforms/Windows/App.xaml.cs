using System.Runtime.InteropServices;
using Horus.Application;
using Horus.Application.Diagnostics;
using Horus.Domain.Models;
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

                // WinUI keeps its own unhandled-exception path: an exception on the UI thread
                // is caught by the XAML framework and raised here, and never reaches
                // AppDomain.UnhandledException. Hooking only the AppDomain would have left the
                // most common crash of a desktop app unrecorded.
                Current.UnhandledException += (_, e) =>
                    CrashHandler.Capture("WinUI", e.Exception, terminating: true);

                Diag.Info("app", $"process start, version {AppConfiguration.AppVersion}");

                var (crashed, at, summary) = CrashHandler.LastCrash();
                if (crashed)
                    Diag.Warn("app", $"previous session ended in a crash at {at:dd.MM HH:mm}", summary);

                // No managed exception and no clean exit: a native abort, which on this app
                // most likely means a panic inside xray-core. Nothing else reports it.
                if (CrashHandler.PreviousSessionEndedAbruptly && !crashed)
                    Diag.Warn("app",
                        "previous session ended without unwinding — no managed exception was raised, " +
                        "which points at a fault in native code rather than in the app");
            }
            catch
            {
                // Diagnostics setup must never be the thing that stops the app starting.
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
