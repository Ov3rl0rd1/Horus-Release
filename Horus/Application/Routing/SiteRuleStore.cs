using System.Text.Json;
using System.Text.Json.Serialization;
using Horus.Domain.Models;

namespace Horus.Application.Routing
{
    /// <summary>One rule as it is written to disk. Kept separate from <see cref="SiteRule"/>
    /// so the stored shape can carry what the user typed as well as what it became.</summary>
    public sealed class StoredSiteRule
    {
        /// <summary>Exactly what the user entered, so the screen can show it back to them.</summary>
        [JsonPropertyName("input")] public string Input { get; set; } = string.Empty;

        [JsonPropertyName("action")] public RuleAction Action { get; set; } = RuleAction.Proxy;
    }

    /// <summary>
    /// The user's own site rules, on disk and in memory.
    ///
    /// <para>Stores what was typed rather than what it normalised to. The two differ — a
    /// pasted URL becomes <c>domain:host</c> — and showing the normalised form back would
    /// mean a user cannot recognise their own entry, or edit it without re-deriving it. The
    /// core only ever sees the normalised list, which is rebuilt on load.</para>
    ///
    /// <para>A bad file is treated as no rules rather than as a failure. These are a
    /// convenience; refusing to connect because a preferences file was truncated would be a
    /// far worse outcome than losing them.</para>
    /// </summary>
    public sealed class SiteRuleStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _path;
        private readonly object _gate = new();

        private List<StoredSiteRule> _stored = [];
        private IReadOnlyList<SiteRule> _compiled = [];

        public SiteRuleStore()
        {
            _path = Path.Combine(FileSystem.AppDataDirectory, "site-rules.json");
            Load();
        }

        /// <summary>What the core is given. Already normalised, deduplicated and validated.</summary>
        public IReadOnlyList<SiteRule> Rules
        {
            get { lock (_gate) return _compiled; }
        }

        /// <summary>What the screen shows.</summary>
        public IReadOnlyList<StoredSiteRule> Entries
        {
            get { lock (_gate) return [.. _stored]; }
        }

        /// <summary>
        /// Adds a rule. Returns false when the input is not something the core would accept —
        /// the caller should say so rather than storing an entry that silently does nothing.
        /// </summary>
        public bool Add(string input, RuleAction action)
        {
            if (SiteRules.Normalise(input) is null) return false;

            lock (_gate)
            {
                _stored.Add(new StoredSiteRule { Input = input.Trim(), Action = action });
                Recompile();
            }

            Save();
            return true;
        }

        public void RemoveAt(int index)
        {
            lock (_gate)
            {
                if (index < 0 || index >= _stored.Count) return;
                _stored.RemoveAt(index);
                Recompile();
            }

            Save();
        }

        /// <summary>Rebuilds the normalised list. Called with the lock held.</summary>
        private void Recompile() =>
            _compiled = SiteRules.Parse(_stored.Select(r => (r.Input, r.Action)));

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;

                var text = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<List<StoredSiteRule>>(text, Json);
                if (parsed is null) return;

                lock (_gate)
                {
                    _stored = parsed;
                    Recompile();
                }
            }
            catch (Exception ex)
            {
                // Losing the rules beats refusing to start over a truncated preferences file.
                Diag.Warn("routing", $"could not read the site rules: {ex.Message}");
            }
        }

        private void Save()
        {
            try
            {
                List<StoredSiteRule> snapshot;
                lock (_gate) snapshot = [.. _stored];

                File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, Json));
            }
            catch (Exception ex)
            {
                Diag.Warn("routing", $"could not save the site rules: {ex.Message}");
            }
        }
    }
}
