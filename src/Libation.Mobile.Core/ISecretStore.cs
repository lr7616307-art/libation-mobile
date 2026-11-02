namespace Libation.Mobile.Core;

/// <summary>
/// Stores small account secrets such as refresh tokens. Implementations must encrypt
/// values at rest and must never log secret names or values.
/// </summary>
public interface ISecretStore
{
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
