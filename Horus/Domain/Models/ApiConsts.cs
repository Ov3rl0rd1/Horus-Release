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

        public const string SERVERS_BEST = "/servers/best";
        public const string SERVERS_CONNECT = "/servers/connect";

        public const string WHOAMI = "/whoami";
        public const string HEALTH = "/health";

        // ── Share-link schemes returned by /servers/connect ──────────────────
        // The endpoint answers with a flat string map whose *keys* are server-side
        // constants we do not control, so links are located by value prefix.
        public const string SCHEME_VLESS = "vless://";
        public const string SCHEME_HYSTERIA2 = "hysteria2://";
        public const string SCHEME_OLCRTC = "olcrtc://";
    }
}
