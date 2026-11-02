using System.Text.Json.Serialization;

namespace Libation.Mobile.Core;

public sealed record Audiobook(
    string Id,
    string Title,
    string Author,
    string Narrator,
    string Length,
    string Status)
{
    [JsonIgnore]
    public bool CanDelete => !string.Equals(Status, "Demo", StringComparison.Ordinal);
}
