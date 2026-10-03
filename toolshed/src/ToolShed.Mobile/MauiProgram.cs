using ToolShed.Mobile.Pages;
using ToolShed.Mobile.Services;

namespace ToolShed.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // One sign-in and one photo cache for the life of the app.
        builder.Services.AddSingleton<PortalSession>();
        builder.Services.AddSingleton<PhotoCache>();

        // Screens are resolved from the container so they can take the services above.
        builder.Services.AddTransient<AppShell>();
        builder.Services.AddTransient<WelcomePage>();
        builder.Services.AddTransient<InvitePage>();
        builder.Services.AddTransient<CataloguePage>();
        builder.Services.AddTransient<MyToolsPage>();
        builder.Services.AddTransient<LoansPage>();
        builder.Services.AddTransient<AccountPage>();
        builder.Services.AddTransient<ToolDetailPage>();
        builder.Services.AddTransient<EditToolPage>();

        return builder.Build();
    }
}
