using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;
using Microsoft.Maui.Platform;

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
            // MAUI 10.0.120 batches initial properties and casts unset (NaN)
            // minimums to integers. Use its normal minimum-property converters
            // afterward; they resolve unset dimensions to zero on 32-bit too.
            Microsoft.Maui.Handlers.ViewHandler.ViewMapper.AppendToMapping("_InitializeBatchedProperties", (handler, view) =>
            {
                if (handler.PlatformView is global::Android.Views.View native)
                {
                    native.UpdateMinimumWidth(view);
                    native.UpdateMinimumHeight(view);
                }
            });
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
