using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horus.Platforms.Windows.Wfp
{
    /// <summary>
    /// Raw interop for the Windows Filtering Platform management API (<c>fwpuclnt.dll</c>).
    ///
    /// <para>Everything here is user mode. WFP splits cleanly in two: <b>filters</b>, which
    /// match a packet or a connection attempt and answer PERMIT or BLOCK, and <b>callouts</b>,
    /// which can inspect and rewrite. Filters can be added from user mode; callouts have to be
    /// registered by a kernel driver. That boundary is why this class only ever blocks and
    /// permits, and why the actual steering of a process's traffic lives elsewhere — see
    /// <see cref="WfpEngine"/>.</para>
    ///
    /// <para><b>Struct layout is load-bearing and untestable from here.</b> These are x64
    /// layouts matched against <c>fwpmtypes.h</c> field by field. A wrong offset does not
    /// throw — WFP reads whatever is at that address, and the usual outcome is a filter that
    /// installs cleanly and matches nothing. Where a C union appears it is represented by the
    /// widest member so the alignment matches, not by the member we happen to use.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class WfpNative
    {
        private const string Dll = "fwpuclnt.dll";

        // ── Session ──────────────────────────────────────────────────────────

        /// <summary>
        /// Everything added on this session disappears when the handle closes — including
        /// when the process dies without closing it.
        ///
        /// <para>This is the single most important flag in the file. Filters are machine-wide
        /// state: a crash midway through installing a block set would otherwise leave a
        /// machine that cannot reach the internet, with nothing in the UI to explain it and
        /// no obvious way to undo it. A dynamic session makes the worst case "split tunneling
        /// stopped working" instead of "the computer is offline until someone finds
        /// netsh wfp".</para>
        /// </summary>
        public const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;

        /// <summary>RPC_C_AUTHN_WINNT — authenticate to the filter engine as the caller.</summary>
        public const uint RPC_C_AUTHN_WINNT = 10;

        // ── Data types (FWP_DATA_TYPE) ───────────────────────────────────────

        public const uint FWP_EMPTY = 0;
        public const uint FWP_UINT8 = 1;
        public const uint FWP_UINT64 = 4;
        public const uint FWP_BYTE_BLOB_TYPE = 12;

        /// <summary>FWP_MATCH_EQUAL.</summary>
        public const uint FWP_MATCH_EQUAL = 0;

        // ── Actions ──────────────────────────────────────────────────────────
        // Both carry FWP_ACTION_FLAG_TERMINATING (0x1000): the decision is final for this
        // sublayer, which is what lets a high-weight PERMIT overrule a low-weight BLOCK.

        public const uint FWP_ACTION_BLOCK = 0x00000001 | 0x00001000;
        public const uint FWP_ACTION_PERMIT = 0x00000002 | 0x00001000;

        // ── Layers and conditions ────────────────────────────────────────────
        // Verified against fwpmu.h as mirrored by OpenVPN's mingw GUID table; a wrong GUID
        // here produces a filter that installs and never matches.

        /// <summary>FWPM_LAYER_ALE_AUTH_CONNECT_V4 — an outbound connection is being authorised.</summary>
        public static readonly Guid LayerAleAuthConnectV4 =
            new("c38d57d1-05a7-4c33-904f-7fbceee60e82");

        /// <summary>FWPM_LAYER_ALE_AUTH_CONNECT_V6.</summary>
        public static readonly Guid LayerAleAuthConnectV6 =
            new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

        /// <summary>
        /// FWPM_CONDITION_ALE_APP_ID — the executable behind the connection, as a normalised
        /// device path. Built by <see cref="FwpmGetAppIdFromFileName0"/>, never by hand.
        /// </summary>
        public static readonly Guid ConditionAleAppId =
            new("d78e1e87-8644-4ea5-9437-d809ecefc971");

        /// <summary>
        /// FWPM_CONDITION_IP_LOCAL_INTERFACE — the interface LUID the connection would leave
        /// by. This is what makes a rule mean "through the tunnel" rather than "at all".
        /// </summary>
        public static readonly Guid ConditionIpLocalInterface =
            new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");

        // ── Structures ───────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_DISPLAY_DATA0
        {
            public IntPtr Name;          // wchar_t*
            public IntPtr Description;   // wchar_t*
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWP_BYTE_BLOB
        {
            public uint Size;
            public IntPtr Data;
        }

        /// <summary>
        /// FWP_VALUE0. The union is pointer-wide on x64: small integers sit in it directly,
        /// while <c>uint64</c> and byte blobs are pointers <b>to</b> the value, which is why
        /// the weight below is heap-allocated rather than assigned inline.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FWP_VALUE0
        {
            public uint Type;
            public IntPtr Value;
        }

        /// <summary>FWP_CONDITION_VALUE0 — same shape as <see cref="FWP_VALUE0"/>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FWP_CONDITION_VALUE0
        {
            public uint Type;
            public IntPtr Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_FILTER_CONDITION0
        {
            public Guid FieldKey;
            public uint MatchType;
            public FWP_CONDITION_VALUE0 ConditionValue;
        }

        /// <summary>FWPM_ACTION0. The union is a GUID we never use, so it stays zeroed.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_ACTION0
        {
            public uint Type;
            public Guid FilterTypeOrCalloutKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_SESSION0
        {
            public Guid SessionKey;
            public FWPM_DISPLAY_DATA0 DisplayData;
            public uint Flags;
            public uint TxnWaitTimeoutInMSec;
            public uint ProcessId;
            public IntPtr Sid;
            public IntPtr Username;
            public int KernelMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_SUBLAYER0
        {
            public Guid SubLayerKey;
            public FWPM_DISPLAY_DATA0 DisplayData;
            public uint Flags;
            public IntPtr ProviderKey;   // GUID*
            public FWP_BYTE_BLOB ProviderData;
            public ushort Weight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FWPM_FILTER0
        {
            public Guid FilterKey;
            public FWPM_DISPLAY_DATA0 DisplayData;
            public uint Flags;
            public IntPtr ProviderKey;   // GUID*
            public FWP_BYTE_BLOB ProviderData;
            public Guid LayerKey;
            public Guid SubLayerKey;
            public FWP_VALUE0 Weight;
            public uint NumFilterConditions;
            public IntPtr FilterCondition;   // FWPM_FILTER_CONDITION0*
            public FWPM_ACTION0 Action;

            // union { UINT64 rawContext; GUID providerContextKey; } — sixteen bytes aligned
            // to eight. Written as two ulongs rather than a Guid on purpose: a Guid aligns to
            // four, which would place every field after it at the wrong offset.
            public ulong ContextLow;
            public ulong ContextHigh;

            public IntPtr Reserved;      // GUID*
            public ulong FilterId;
            public FWP_VALUE0 EffectiveWeight;
        }

        // ── Entry points ─────────────────────────────────────────────────────

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmEngineOpen0(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            uint authnService,
            IntPtr authIdentity,
            ref FWPM_SESSION0 session,
            out IntPtr engineHandle);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmEngineClose0(IntPtr engineHandle);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmSubLayerAdd0(
            IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmFilterAdd0(
            IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

        /// <summary>
        /// Turns a file path into the normalised device path WFP matches on
        /// (<c>\device\harddiskvolume3\…</c>). The result is allocated by WFP and must be
        /// released with <see cref="FwpmFreeMemory0"/>.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern uint FwpmGetAppIdFromFileName0(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName, out IntPtr appId);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern void FwpmFreeMemory0(ref IntPtr p);
    }
}
