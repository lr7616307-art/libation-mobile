using System.Security.Cryptography;
using Android.Content;

namespace Libation.Mobile.Android;

/// <summary>
/// Stores a random, app-install-scoped Audible device serial in private app storage.
/// It is not derived from IMEI, serial number, MAC address, or other hardware identifiers.
/// </summary>
public sealed class AudibleAndroidDeviceSerialStore
{
    private const string PreferencesName = "vorath_device_identity";
    private const string SerialKey = "audible_android_device_serial_v1";
    private static readonly object Gate = new();
    private readonly ISharedPreferences preferences;

    public AudibleAndroidDeviceSerialStore(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Unable to open private device-identity storage.");
    }

    public string GetOrCreate()
    {
        lock (Gate)
        {
            var existing = preferences.GetString(SerialKey, null);
            if (existing is not null)
            {
                if (!IsValid(existing))
                    throw new CryptographicException("The saved Audible installation identity is invalid; it was not replaced.");
                return existing;
            }

            var created = Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
            if (!preferences.Edit()!.PutString(SerialKey, created)!.Commit())
                throw new IOException("Unable to persist the Audible installation identity.");
            return created;
        }
    }

    private static bool IsValid(string value)
        => value.Length == 20 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
