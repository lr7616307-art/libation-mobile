namespace Libation.Mobile.Core;

public sealed record Audiobook(
    string Id,
    string Title,
    string Author,
    string Narrator,
    string Length,
    string Status);
