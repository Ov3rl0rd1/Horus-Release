namespace Horus.Domain.Models
{
    public record User(string username, DateTime? expiresAt);
}
