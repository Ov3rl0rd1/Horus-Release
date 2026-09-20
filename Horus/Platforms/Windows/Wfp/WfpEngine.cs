using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Horus.Domain.Models;

namespace Horus.Platforms.Windows.Wfp
{
    /// <summary>Thrown when the filter engine refuses an operation; carries the Win32 code.</summary>
    public sealed class WfpException(string what, uint code)
        : Exception($"{what} failed: 0x{code:X8}")
    {
        public uint Code { get; } = code;
    }

    /// <summary>
    /// A live WFP session with its own sublayer, holding the filters that decide which
    /// processes may use the tunnel interface.
    ///
    /// <para><b>What this can and cannot do.</b> Filters answer PERMIT or BLOCK; they cannot
    /// move a connection to a different interface, because that is a redirect and redirects
    /// happen at <c>ALE_BIND_REDIRECT</c>/<c>ALE_CONNECT_REDIRECT</c>, layers that only a
    /// kernel callout driver can attach to. So the division of labour is: the route table
    /// decides where traffic goes by default, WinDivert steers the exceptions per process,
    /// and this class makes the arrangement <i>fail closed</i> — if a flow is not steered, it
    /// is blocked rather than quietly sent the wrong way.</para>
    ///
    /// <para><b>Everything is on a dynamic session.</b> Every object added here is destroyed
    /// when the handle closes, including when the process dies without closing it. Filters
    /// are machine-wide state, and the failure that has to be impossible is a crash leaving a
    /// machine that cannot reach the internet with nothing to explain why.</para>
    ///
    /// <para>Weights are explicit rather than left to WFP. Auto-assigned weights rank filters
    /// by how specific their conditions are, which happens to put a two-condition PERMIT
    /// above a one-condition BLOCK — true today, not promised, and the whole scheme depends
    /// on it.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WfpEngine : IDisposable
    {
        /// <summary>Blanket rules: "nothing may use this interface".</summary>
        private const ulong WeightBlanket = 1000;

        /// <summary>Per-application rules. Above the blanket, so an exception wins.</summary>
        private const ulong WeightApp = 2000;

        private static readonly Guid SubLayerKey = new("5f1d9a0e-1c1b-4b1e-9a2e-7a5c9f3b6d21");

        private IntPtr _engine;
        private readonly List<ulong> _filters = [];

        public bool IsOpen => _engine != IntPtr.Zero;

        /// <summary>
        /// Opens the engine and installs our sublayer. Needs administrator rights, which the
        /// Windows build already has — creating the TUN adapter requires them too, so there
        /// is no case where the tunnel is up and this cannot run.
        /// </summary>
        public void Open()
        {
            if (IsOpen) return;

            AssertLayouts();

            var session = new WfpNative.FWPM_SESSION0
            {
                Flags = WfpNative.FWPM_SESSION_FLAG_DYNAMIC
            };

            var status = WfpNative.FwpmEngineOpen0(
                null, WfpNative.RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out _engine);

            if (status != 0)
            {
                _engine = IntPtr.Zero;
                throw new WfpException("FwpmEngineOpen0", status);
            }

            try { AddSubLayer(); }
            catch { Dispose(); throw; }
        }

        private void AddSubLayer()
        {
            var name = Marshal.StringToHGlobalUni("Horus split tunneling");
            try
            {
                var sub = new WfpNative.FWPM_SUBLAYER0
                {
                    SubLayerKey = SubLayerKey,
                    DisplayData = new WfpNative.FWPM_DISPLAY_DATA0 { Name = name },
                    Weight = ushort.MaxValue
                };

                var status = WfpNative.FwpmSubLayerAdd0(_engine, ref sub, IntPtr.Zero);

                // FWP_E_ALREADY_EXISTS. Cannot happen on a fresh dynamic session, but a
                // previous session of ours that has not finished being torn down can still
                // own the key for a moment; reusing it is correct either way.
                if (status != 0 && status != 0x80320009)
                    throw new WfpException("FwpmSubLayerAdd0", status);
            }
            finally { Marshal.FreeHGlobal(name); }
        }

        // ── Rules ────────────────────────────────────────────────────────────

        /// <summary>
        /// Nothing may open a connection over this interface. The starting point for
        /// <see cref="SplitTunnelingMode.Whitelist"/>, where the exceptions are added on top.
        /// </summary>
        public void BlockInterface(ulong interfaceLuid) =>
            AddFilter(null, interfaceLuid, WfpNative.FWP_ACTION_BLOCK, WeightBlanket,
                "Horus: block all on tunnel");

        /// <summary>This one application may use the interface, whatever the blanket says.</summary>
        public void PermitApp(string exePath, ulong interfaceLuid) =>
            AddFilter(exePath, interfaceLuid, WfpNative.FWP_ACTION_PERMIT, WeightApp,
                "Horus: permit app on tunnel");

        /// <summary>
        /// This one application may not use the interface. The fail-closed half of
        /// <see cref="SplitTunnelingMode.Blacklist"/>: the app is supposed to have been
        /// steered onto the physical path, and if that did not happen it must not silently
        /// fall back into the tunnel it was meant to bypass.
        /// </summary>
        public void BlockApp(string exePath, ulong interfaceLuid) =>
            AddFilter(exePath, interfaceLuid, WfpNative.FWP_ACTION_BLOCK, WeightApp,
                "Horus: keep app off tunnel");

        /// <summary>
        /// Removes every filter this session added, leaving the engine open. Used when the
        /// selection changes: cheaper and less racy than tearing the session down and
        /// rebuilding it, which would briefly leave the machine unfiltered.
        /// </summary>
        public void ClearFilters()
        {
            if (!IsOpen) return;

            foreach (var id in _filters)
            {
                // A filter that is already gone is not a failure worth propagating — the
                // caller's next step is to add the new set either way.
                try { WfpNative.FwpmFilterDeleteById0(_engine, id); } catch { }
            }

            _filters.Clear();
        }

        /// <summary>
        /// One filter, added for both IPv4 and IPv6. Adding only v4 is the classic way to
        /// build a leak: a machine with working IPv6 simply uses it and every v4 rule is
        /// irrelevant.
        /// </summary>
        private void AddFilter(string? exePath, ulong interfaceLuid, uint action, ulong weight, string label)
        {
            if (!IsOpen) throw new InvalidOperationException("The WFP engine is not open.");

            AddFilterAtLayer(WfpNative.LayerAleAuthConnectV4, exePath, interfaceLuid, action, weight, label);
            AddFilterAtLayer(WfpNative.LayerAleAuthConnectV6, exePath, interfaceLuid, action, weight, label);
        }

        private void AddFilterAtLayer(
            Guid layer, string? exePath, ulong interfaceLuid, uint action, ulong weight, string label)
        {
            var allocations = new List<IntPtr>();
            IntPtr appId = IntPtr.Zero;

            try
            {
                var conditions = new List<WfpNative.FWPM_FILTER_CONDITION0>();

                // The interface condition is what makes every rule here mean "on the tunnel"
                // rather than "anywhere". Without it a block rule would take the machine off
                // the network entirely.
                var luidPtr = Alloc(allocations, sizeof(ulong));
                Marshal.WriteInt64(luidPtr, (long)interfaceLuid);
                conditions.Add(new WfpNative.FWPM_FILTER_CONDITION0
                {
                    FieldKey = WfpNative.ConditionIpLocalInterface,
                    MatchType = WfpNative.FWP_MATCH_EQUAL,
                    ConditionValue = new WfpNative.FWP_CONDITION_VALUE0
                    {
                        Type = WfpNative.FWP_UINT64,
                        Value = luidPtr
                    }
                });

                if (exePath is not null)
                {
                    // WFP matches on a normalised device path, not on the path the user sees.
                    // Building it by hand is the usual way to get a filter that installs and
                    // never fires.
                    var status = WfpNative.FwpmGetAppIdFromFileName0(exePath, out appId);
                    if (status != 0) throw new WfpException($"FwpmGetAppIdFromFileName0({exePath})", status);

                    conditions.Add(new WfpNative.FWPM_FILTER_CONDITION0
                    {
                        FieldKey = WfpNative.ConditionAleAppId,
                        MatchType = WfpNative.FWP_MATCH_EQUAL,
                        ConditionValue = new WfpNative.FWP_CONDITION_VALUE0
                        {
                            Type = WfpNative.FWP_BYTE_BLOB_TYPE,
                            Value = appId
                        }
                    });
                }

                var conditionsPtr = AllocArray(allocations, conditions);
                var weightPtr = Alloc(allocations, sizeof(ulong));
                Marshal.WriteInt64(weightPtr, (long)weight);

                var namePtr = Marshal.StringToHGlobalUni(label);
                allocations.Add(namePtr);

                var filter = new WfpNative.FWPM_FILTER0
                {
                    DisplayData = new WfpNative.FWPM_DISPLAY_DATA0 { Name = namePtr },
                    LayerKey = layer,
                    SubLayerKey = SubLayerKey,
                    Weight = new WfpNative.FWP_VALUE0
                    {
                        Type = WfpNative.FWP_UINT64,
                        Value = weightPtr
                    },
                    NumFilterConditions = (uint)conditions.Count,
                    FilterCondition = conditionsPtr,
                    Action = new WfpNative.FWPM_ACTION0 { Type = action }
                };

                var added = WfpNative.FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out var id);
                if (added != 0) throw new WfpException("FwpmFilterAdd0", added);

                _filters.Add(id);
            }
            finally
            {
                // WFP copies everything it is given, so none of this has to outlive the call.
                if (appId != IntPtr.Zero) WfpNative.FwpmFreeMemory0(ref appId);
                foreach (var p in allocations) Marshal.FreeHGlobal(p);
            }
        }

        private static IntPtr Alloc(List<IntPtr> track, int bytes)
        {
            var p = Marshal.AllocHGlobal(bytes);
            track.Add(p);
            return p;
        }

        private static IntPtr AllocArray(
            List<IntPtr> track, List<WfpNative.FWPM_FILTER_CONDITION0> items)
        {
            var size = Marshal.SizeOf<WfpNative.FWPM_FILTER_CONDITION0>();
            var block = Alloc(track, size * items.Count);

            for (var i = 0; i < items.Count; i++)
                Marshal.StructureToPtr(items[i], block + i * size, false);

            return block;
        }

        /// <summary>
        /// Checks the marshalled size of every structure against the x64 layout in
        /// <c>fwpmtypes.h</c> before the engine is touched.
        ///
        /// <para>This exists because the failure it catches is silent. WFP takes a pointer
        /// and reads fields at fixed offsets; if a field has shifted, the call still returns
        /// success and installs a filter whose conditions point at whatever happened to be
        /// at that address. The rule then matches nothing, split tunneling appears to be on
        /// and does nothing, and there is no error anywhere to explain it.</para>
        ///
        /// <para>Sizes are a coarse check — they cannot catch two fields that moved by the
        /// same amount in opposite directions — but every realistic breakage here (a runtime
        /// changing how it packs a nested struct, someone adding a field, <c>Guid</c>
        /// alignment differing from the C union it stands in for) changes at least one
        /// size.</para>
        /// </summary>
        private static void AssertLayouts()
        {
            Check<WfpNative.FWPM_DISPLAY_DATA0>(16);
            Check<WfpNative.FWP_BYTE_BLOB>(16);
            Check<WfpNative.FWP_VALUE0>(16);
            Check<WfpNative.FWP_CONDITION_VALUE0>(16);
            Check<WfpNative.FWPM_FILTER_CONDITION0>(40);
            Check<WfpNative.FWPM_ACTION0>(20);
            Check<WfpNative.FWPM_SESSION0>(72);
            Check<WfpNative.FWPM_SUBLAYER0>(72);
            Check<WfpNative.FWPM_FILTER0>(200);

            static void Check<T>(int expected) where T : struct
            {
                var actual = Marshal.SizeOf<T>();
                if (actual != expected)
                    throw new InvalidOperationException(
                        $"WFP interop layout drift: {typeof(T).Name} marshals to {actual} bytes, " +
                        $"expected {expected}. Filters built from this would install and match nothing.");
            }
        }

        // ── Interface lookup ─────────────────────────────────────────────────

        /// <summary>
        /// The LUID of an adapter by its alias ("Horus", "Wi-Fi"). Null when there is no such
        /// adapter, which the caller must read as "do not install interface-scoped rules" —
        /// a rule with the wrong LUID is worse than no rule at all.
        /// </summary>
        public static ulong? LookupInterfaceLuid(string alias)
        {
            var status = ConvertInterfaceAliasToLuid(alias, out var luid);
            return status == 0 ? luid : null;
        }

        [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint ConvertInterfaceAliasToLuid(string interfaceAlias, out ulong interfaceLuid);

        // ── Teardown ─────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (!IsOpen) return;

            // Closing the handle is what actually removes the filters, because the session is
            // dynamic. Deleting them first is politeness, not correctness.
            ClearFilters();

            WfpNative.FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
    }
}
