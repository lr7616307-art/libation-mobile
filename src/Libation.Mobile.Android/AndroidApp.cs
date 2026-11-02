using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Libation.Mobile.UI;

namespace Libation.Mobile.Android;

[Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }
}
