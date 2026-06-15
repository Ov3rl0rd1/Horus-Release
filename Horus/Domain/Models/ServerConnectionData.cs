namespace Horus.Domain.Models
{
    public record ServerConnectionData(
        int ServerId,
        string Host,
        string Protocol,
        string ObfsType,
        string ObfsPassword,
        string Hop,
        string Template
    );
}
