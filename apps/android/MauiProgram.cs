using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;

namespace StreamedTv
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            ServiceLocator.Initialize();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkitMediaElement(false, options => options.SetDefaultAndroidViewType(AndroidViewType.TextureView));

#if ANDROID
            builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<ContentPage, Platforms.Android.BoundedPageHandler>());
            Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("RemoteFocus", (handler, _) =>
            {
                handler.PlatformView.Focusable = true;
                handler.PlatformView.FocusableInTouchMode = true;
            });
            Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("StreamedPlayback", (handler, _) =>
            {
                handler.PlatformView.Settings.JavaScriptEnabled = true;
                handler.PlatformView.Settings.DomStorageEnabled = true;
                handler.PlatformView.Settings.MediaPlaybackRequiresUserGesture = false;
                handler.PlatformView.Settings.JavaScriptCanOpenWindowsAutomatically = false;
            });
#endif

#if DEBUG
            builder.Logging.AddDebug();
#endif
            return builder.Build();
        }
    }
}
