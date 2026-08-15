using System.Text.RegularExpressions;

namespace Horus.Application.Update
{
    /// <summary>
    /// Turns the human-facing releases URL from appsettings into the API endpoint.
    ///
    /// Separated out and kept free of dependencies so it can be tested: the configured
    /// value is a string a person pastes from a browser, and every way of getting it
    /// slightly wrong — a trailing slash, a <c>.git</c> suffix, the bare repository URL with
    /// no <c>/releases</c> — has to keep working. Getting it wrong is silent, because the
    /// updater treats an unreachable GitHub as normal and falls through to the site.
    /// </summary>
    public static partial class GitHubApi
    {
        [GeneratedRegex(@"github\.com/([^/]+)/([^/#?]+?)(?:\.git)?(?:/releases.*)?/?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex RepoPath();

        /// <summary>
        /// The releases endpoint for the repository named in <paramref name="releasesUrl"/>,
        /// or null when nothing usable can be read out of it. Twenty per page: enough to
        /// find the newest release when several pre-releases have been published since the
        /// last stable one, without paging.
        /// </summary>
        public static string? BuildReleasesUrl(string? releasesUrl)
        {
            if (string.IsNullOrWhiteSpace(releasesUrl)) return null;

            var match = RepoPath().Match(releasesUrl.Trim());
            if (!match.Success) return null;

            var owner = match.Groups[1].Value;
            var repo = match.Groups[2].Value;
            if (owner.Length == 0 || repo.Length == 0) return null;

            return $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20";
        }
    }
}
