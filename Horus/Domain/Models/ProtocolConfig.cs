namespace Horus.Domain.Models
{
    public abstract class ProtocolConfig
    {
        public string ServerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>
        /// Stable id of the node offer this config came from, e.g. <c>vless-reality</c>.
        ///
        /// A string rather than an enum since the API stopped naming protocols: the node
        /// ships whole outbounds, so it can offer one this build has never heard of and
        /// the fallback loop still has something to key on.
        /// </summary>
        public abstract string OfferId { get; }

        /// <summary>What to show the user. Supplied by the node.</summary>
        public abstract string DisplayName { get; }
        public abstract string ToConfig();
    }
}
