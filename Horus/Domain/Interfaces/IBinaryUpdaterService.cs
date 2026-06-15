using Horus.Domain.Models;

namespace Horus.Domain.Interfaces
{
    public interface IBinaryUpdaterService
    {
        Task<BinaryUpdateInfo> CheckForUpdateAsync(string binaryName, CancellationToken ct = default);
        Task<string> DownloadAndInstallAsync(BinaryUpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);
        string GetInstalledBinaryPath(string binaryName);
    }
}
