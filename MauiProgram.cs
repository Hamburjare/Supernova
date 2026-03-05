using Microsoft.Extensions.Logging;
using Supernova.Services;
using Supernova.ViewModels;

namespace Supernova;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Services
        builder.Services.AddSingleton<SpacetimeService>();
        builder.Services.AddSingleton<AudioService>();

        // ViewModels
        builder.Services.AddSingleton<MainViewModel>();

        // Pages
        builder.Services.AddSingleton<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
