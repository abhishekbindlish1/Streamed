using Android.App;
using Android.Runtime;

namespace StreamedTv;

#if SANDBOX
[Application(NetworkSecurityConfig = "@xml/sandbox_network_security_config")]
#else
[Application]
#endif
public sealed class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
