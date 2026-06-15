namespace Horus.Domain.Models
{
    public class BinaryUpdateInfo
    {
        public string BinaryName { get; set; } = string.Empty;
        public string? CurrentVersion { get; set; }
        public string LatestVersion { get; set; } = string.Empty;
        public bool UpdateAvailable { get; set; }
        public string DownloadUrl { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
    }
}
