namespace Horus.Domain.Models
{
    public record LoginResponse(string session, DateTime? expiresAt);
}
