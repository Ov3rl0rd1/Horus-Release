namespace Horus.Domain.Models
{
    /// <summary>
    /// Which outbound of the bundled xray-core the tunnel proxies through.
    /// These are no longer separate binaries — one xray process is started and
    /// the selected outbound becomes its <c>proxy</c> tag.
    /// </summary>
    public enum ProtocolType
    {
        Hysteria2,
        Vless,
        OlcRtc
    }
}
