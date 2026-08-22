using Android.App;
using Android.Runtime;
using Horus.Application;
using Horus.Application.Diagnostics;
using Horus.Domain.Models;
using Horus.Platforms.Android;

namespace Horus
{
    [Application]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
        }

        public override void OnCreate()
        {
            base.OnCreate();

            // Diagnostics before anything else, deliberately.
            //
            // The crash handler has to be installed before the DI container is built and
            // before MAUI initialises, or a failure during startup — the hardest kind to
            // reproduce and the one most likely to be reported as "it just doesn't open" —
            // goes unrecorded. EventLog.Install comes first of the two so the handler has
            // somewhere to flush to.
            try
            {
                EventLog.Install();
                UserPreferences.ApplyLogLevel();
                CrashHandler.Install();

                Diag.Info("app", $"process start, version {AppConfiguration.AppVersion}");

                var (crashed, at, summary) = CrashHandler.LastCrash();
                if (crashed)
                    Diag.Warn("app", $"previous session ended in a crash at {at:dd.MM HH:mm}", summary);
            }
            catch (Exception ex)
            {
                // Never let diagnostics setup be the thing that stops the app starting.
                global::Android.Util.Log.Error("Horus", $"diagnostics init failed: {ex}");
            }

            try { NativeInventory.Register(); }
            catch (Exception ex) { Diag.Warn("app", $"native inventory failed: {ex.Message}"); }
        }

        public override void OnTrimMemory([global::Android.Runtime.GeneratedEnum] global::Android.Content.TrimMemory level)
        {
            base.OnTrimMemory(level);
            Diag.Trace("app", $"trim memory: {level}");

            if (level < global::Android.Content.TrimMemory.Background) return;

            // Both heaps, because the managed one is the smaller half. xray-core is Go and
            // holds released pages rather than handing them back, so a process resident for
            // weeks only grows — and a large process is the first thing the OOM killer
            // reaches for. XrayForceGc is asynchronous inside the library, so this returns
            // immediately; the callback must not block.
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            Horus.Protocols.XrayProtocol.ForceGc();
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
