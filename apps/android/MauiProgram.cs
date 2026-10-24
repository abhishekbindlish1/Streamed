using Microsoft.Extensions.Logging;

namespace StreamedTv
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            ServiceLocator.Initialize();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>();

#if DEBUG
            builder.Logging.AddDebug();
#endif
            return builder.Build();
        }
    }
}
