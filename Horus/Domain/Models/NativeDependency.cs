namespace Horus.Domain.Models
{
    /// <summary>
    /// A native binary the app needs at runtime, and what breaks without it.
    /// </summary>
    /// <param name="FileName">File as it must be named on disk.</param>
    /// <param name="RelativeDirectory">
    /// Location relative to the app directory. Empty means next to the executable.
    /// </param>
    /// <param name="Required">
    /// True when the app is unusable without it. Optional ones only disable a feature.
    /// </param>
    /// <param name="Purpose">One line on what it provides, shown to the user.</param>
    public sealed record NativeDependency(
        string FileName,
        string RelativeDirectory,
        bool Required,
        string Purpose)
    {
        /// <summary>Absolute path this file is expected at.</summary>
        public string ExpectedPath => Path.Combine(
            AppContext.BaseDirectory, RelativeDirectory, FileName);

        public bool Exists => File.Exists(ExpectedPath);

        /// <summary>Path shown to the user — relative, since the app directory varies.</summary>
        public string DisplayPath => string.IsNullOrEmpty(RelativeDirectory)
            ? FileName
            : Path.Combine(RelativeDirectory, FileName);
    }

    /// <summary>Outcome of the startup dependency check.</summary>
    /// <param name="Missing">Required files that are absent.</param>
    /// <param name="LoadFailure">
    /// Set when a required file is present but unusable — wrong architecture, blocked by
    /// SmartScreen, or missing its own dependencies. Existence alone does not prove a
    /// native library will load.
    /// </param>
    public sealed record NativeDependencyReport(
        IReadOnlyList<NativeDependency> Missing,
        string? LoadFailure)
    {
        public bool IsSatisfied => Missing.Count == 0 && LoadFailure is null;
    }
}
