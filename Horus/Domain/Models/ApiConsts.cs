namespace Horus.Domain.Models
{
    internal static class ApiConsts
    {
        public const string SESSION_HEADER = "X-Session-Key";

        // ── Route templates (HorusAPI v1) ────────────────────────────────────
        public const string AUTH_LOGIN = "/auth/login";
        public const string AUTH_REGISTER = "/auth/register";
        public const string AUTH_VERIFY = "/auth/verify";
        public const string AUTH_RESEND_CODE = "/auth/resend-code";
        public const string AUTH_RESET_REQUEST = "/auth/reset-request";
        public const string AUTH_RESET_CHECK = "/auth/reset-check";
        public const string AUTH_RESET_CONFIRM = "/auth/reset-confirm";
        public const string AUTH_LOGOUT_OTHERS = "/auth/logout-others";

        /// <summary>
        /// Ping candidates: the least-loaded active node <b>with free capacity</b> in each
        /// country, ordered by load. The client is expected to measure them and choose —
        /// which is why this is a plain catalogue and binds nothing.
        /// </summary>
        public const string SERVERS = "/servers";

        /// <summary>
        /// Reserves a slot on a node and binds the account to it. An empty body (or a null
        /// <c>server_id</c>) means "pick the least loaded".
        ///
        /// <para>Binding is now an explicit step. It used to be a side effect of
        /// <see cref="SERVERS_CONNECT"/>, which meant the app could not choose a node at
        /// all; capacity is reserved here, so this is also where <c>409 no_capacity</c>
        /// comes from.</para>
        /// </summary>
        public const string SERVERS_SELECT = "/servers/select";

        /// <summary>
        /// Connection links for the node the caller is already bound to. Cheap by design —
        /// it reads one row and never talks to the node, because provisioning happens at
        /// <see cref="SERVERS_SELECT"/> time.
        /// </summary>
        public const string SERVERS_CONNECT = "/servers/connect";

        public const string WHOAMI = "/whoami";
        public const string HEALTH = "/health";

        // ── Share-link schemes ───────────────────────────────────────────────
        // vless and hysteria2 arrive as share links. olcRTC does not: it is signalling
        // based and has no address to put in a URI, so the API sends a structured object
        // instead — see OlcRtcEndpoint.
        public const string SCHEME_VLESS = "vless://";
        public const string SCHEME_HYSTERIA2 = "hysteria2://";
    }
}
