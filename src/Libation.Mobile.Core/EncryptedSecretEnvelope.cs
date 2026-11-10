namespace Libation.Mobile.Core;

/// <summary>
/// Versioned AES-GCM envelope framing shared by platform secret stores and tests.
/// Cryptographic operations and key custody remain the platform store's responsibility.
/// </summary>
public static class EncryptedSecretEnvelope
{
    public const byte CurrentVersion = 2;
    public const int GcmIvLength = 12;
    public const int GcmTagLength = 16;
    public const int MaximumPlaintextBytes = 128 * 1024;

    public static byte[] Encode(byte[] iv, byte[] ciphertextAndTag)
    {
        ArgumentNullException.ThrowIfNull(iv);
        ArgumentNullException.ThrowIfNull(ciphertextAndTag);
        if (iv.Length != GcmIvLength)
            throw new ArgumentException("AES-GCM IV must be 12 bytes.", nameof(iv));
        if (ciphertextAndTag.Length < GcmTagLength)
            throw new ArgumentException("AES-GCM payload must include its authentication tag.", nameof(ciphertextAndTag));

        var payload = new byte[2 + iv.Length + ciphertextAndTag.Length];
        payload[0] = CurrentVersion;
        payload[1] = checked((byte)iv.Length);
        Buffer.BlockCopy(iv, 0, payload, 2, iv.Length);
        Buffer.BlockCopy(ciphertextAndTag, 0, payload, 2 + iv.Length, ciphertextAndTag.Length);
        return payload;
    }

    public static EncryptedSecretEnvelopeData Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < 1)
            throw new InvalidDataException("Stored secret envelope is empty.");

        var legacy = payload[0] == GcmIvLength;
        var headerLength = legacy ? 1 : 2;
        if (!legacy && (payload[0] != CurrentVersion || payload.Length < headerLength))
            throw new InvalidDataException("Stored secret envelope version is unsupported.");

        var ivLength = legacy ? payload[0] : payload[1];
        if (ivLength != GcmIvLength || payload.Length < headerLength + ivLength + GcmTagLength)
            throw new InvalidDataException("Stored secret envelope is malformed.");

        var iv = new byte[ivLength];
        var ciphertextAndTag = new byte[payload.Length - headerLength - ivLength];
        Buffer.BlockCopy(payload, headerLength, iv, 0, iv.Length);
        Buffer.BlockCopy(payload, headerLength + ivLength, ciphertextAndTag, 0, ciphertextAndTag.Length);
        return new EncryptedSecretEnvelopeData(legacy, iv, ciphertextAndTag);
    }

    public static byte[] GetAssociatedData(string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        return System.Text.Encoding.UTF8.GetBytes(secretKey);
    }
}

public sealed record EncryptedSecretEnvelopeData(bool IsLegacy, byte[] Iv, byte[] CiphertextAndTag);
