namespace Libation.Mobile.Audible;

/// <summary>
/// Non-secret hardware/runtime facts supplied by the Android host. Registration
/// requests must describe the actual host device rather than a hard-coded emulator.
/// </summary>
public sealed record AudibleAndroidDeviceInfo(
    string Manufacturer,
    string Brand,
    string Model,
    string Device,
    string Product,
    string BuildFingerprint,
    string OsRelease,
    string SdkVersion,
    string DeviceName,
    string DeviceLanguage,
    int ScreenWidthPixels,
    int ScreenHeightPixels)
{
    public void Validate()
    {
        Require(Manufacturer, nameof(Manufacturer));
        Require(Brand, nameof(Brand));
        Require(Model, nameof(Model));
        Require(Device, nameof(Device));
        Require(Product, nameof(Product));
        Require(BuildFingerprint, nameof(BuildFingerprint));
        Require(OsRelease, nameof(OsRelease));
        Require(SdkVersion, nameof(SdkVersion));
        Require(DeviceName, nameof(DeviceName));
        Require(DeviceLanguage, nameof(DeviceLanguage));

        if (!int.TryParse(SdkVersion, out var sdk) || sdk < 1)
            throw new ArgumentException("Android SDK version is invalid.", nameof(SdkVersion));
        if (ScreenWidthPixels is < 1 or > 20000)
            throw new ArgumentOutOfRangeException(nameof(ScreenWidthPixels));
        if (ScreenHeightPixels is < 1 or > 20000)
            throw new ArgumentOutOfRangeException(nameof(ScreenHeightPixels));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            throw new ArgumentException("Android device metadata is missing or too long.", name);
    }
}
