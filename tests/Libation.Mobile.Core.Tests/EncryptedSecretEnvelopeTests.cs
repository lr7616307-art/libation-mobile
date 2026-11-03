using System.Security.Cryptography;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class EncryptedSecretEnvelopeTests
{
    [Fact]
    public void Version2Envelope_RoundTripsIvAndCiphertextAndTag()
    {
        var iv = RandomNumberGenerator.GetBytes(EncryptedSecretEnvelope.GcmIvLength);
        var ciphertextAndTag = RandomNumberGenerator.GetBytes(37);

        var encoded = EncryptedSecretEnvelope.Encode(iv, ciphertextAndTag);
        var decoded = EncryptedSecretEnvelope.Decode(encoded);

        Assert.False(decoded.IsLegacy);
        Assert.Equal(iv, decoded.Iv);
        Assert.Equal(ciphertextAndTag, decoded.CiphertextAndTag);
        Assert.Equal(EncryptedSecretEnvelope.CurrentVersion, encoded[0]);
    }

    [Fact]
    public void LegacyEnvelope_IsRecognizedForMigration()
    {
        var iv = RandomNumberGenerator.GetBytes(EncryptedSecretEnvelope.GcmIvLength);
        var ciphertextAndTag = RandomNumberGenerator.GetBytes(EncryptedSecretEnvelope.GcmTagLength + 5);
        var payload = new byte[1 + iv.Length + ciphertextAndTag.Length];
        payload[0] = (byte)iv.Length;
        Buffer.BlockCopy(iv, 0, payload, 1, iv.Length);
        Buffer.BlockCopy(ciphertextAndTag, 0, payload, 1 + iv.Length, ciphertextAndTag.Length);

        var decoded = EncryptedSecretEnvelope.Decode(payload);

        Assert.True(decoded.IsLegacy);
        Assert.Equal(iv, decoded.Iv);
        Assert.Equal(ciphertextAndTag, decoded.CiphertextAndTag);
    }

    [Fact]
    public void UnknownVersion_IsRejected()
    {
        var payload = new byte[] { 3, 12, 1, 2, 3 };

        Assert.Throws<InvalidDataException>(() => EncryptedSecretEnvelope.Decode(payload));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 2 })]
    [InlineData(new byte[] { 2, 8, 1, 2, 3 })]
    [InlineData(new byte[] { 2, 12, 1, 2, 3 })]
    [InlineData(new byte[] { 12, 1, 2, 3 })]
    public void MalformedOrTruncatedEnvelope_IsRejected(byte[] payload)
    {
        Assert.Throws<InvalidDataException>(() => EncryptedSecretEnvelope.Decode(payload));
    }

    [Fact]
    public void CiphertextCopiedToDifferentSecretKey_FailsAuthentication()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("test-only-secret");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(iv, plaintext, ciphertext, tag, EncryptedSecretEnvelope.GetAssociatedData("account_token"));

        var ciphertextAndTag = ciphertext.Concat(tag).ToArray();
        var envelope = EncryptedSecretEnvelope.Decode(EncryptedSecretEnvelope.Encode(iv, ciphertextAndTag));
        var restored = new byte[plaintext.Length];

        using var decryptor = new AesGcm(key, tag.Length);
        Assert.Throws<CryptographicException>(() =>
            decryptor.Decrypt(envelope.Iv,
                envelope.CiphertextAndTag.AsSpan(0, plaintext.Length),
                envelope.CiphertextAndTag.AsSpan(plaintext.Length, tag.Length),
                restored,
                EncryptedSecretEnvelope.GetAssociatedData("different_secret_key")));
    }

    [Fact]
    public void TamperedIv_FailsAuthentication()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("test-only-secret");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(iv, plaintext, ciphertext, tag, EncryptedSecretEnvelope.GetAssociatedData("test_key"));

        var alteredIv = (byte[])iv.Clone();
        alteredIv[0] ^= 0x40;
        var restored = new byte[plaintext.Length];

        using var decryptor = new AesGcm(key, tag.Length);
        Assert.Throws<CryptographicException>(() =>
            decryptor.Decrypt(alteredIv, ciphertext, tag, restored,
                EncryptedSecretEnvelope.GetAssociatedData("test_key")));
    }

    [Fact]
    public void TamperedCiphertext_FailsAuthentication()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("test-only-secret");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(iv, plaintext, ciphertext, tag, EncryptedSecretEnvelope.GetAssociatedData("test_key"));

        ciphertext[0] ^= 0x01;
        var restored = new byte[plaintext.Length];

        using var decryptor = new AesGcm(key, tag.Length);
        Assert.Throws<CryptographicException>(() =>
            decryptor.Decrypt(iv, ciphertext, tag, restored,
                EncryptedSecretEnvelope.GetAssociatedData("test_key")));
    }
}
