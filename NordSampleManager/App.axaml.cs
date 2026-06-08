using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using NordSampleManager.Services;
using NordSampleManager.ViewModels;
using NordSampleManager.Views;

namespace NordSampleManager;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DeviceService>();
        services.AddSingleton<SoundLibrary>();
        services.AddSingleton(_ =>
        {
            var http = new System.Net.Http.HttpClient
            {
                BaseAddress = new Uri("https://www.nordkeyboards.com"),
                Timeout     = TimeSpan.FromMinutes(5),
            };
            http.DefaultRequestHeaders.Add("Accept", "application/json, text/html");
            return http;
        });
        services.AddSingleton<NordLibraryClient>();
        services.AddSingleton<SoundLibraryViewModel>();
        services.AddTransient<MainWindowViewModel>();
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
