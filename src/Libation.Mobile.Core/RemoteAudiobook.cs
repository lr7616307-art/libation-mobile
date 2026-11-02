namespace Libation.Mobile.Core;

/// <summary>Metadata returned by an authorized audiobook-catalogue provider adapter.</summary>
public sealed record RemoteAudiobook(string ProviderId, string Title, string Author, string Narrator, string Length);
