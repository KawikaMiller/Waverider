using Microsoft.UI.Xaml;
using Waverider.Theming;

namespace Waverider;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        AppHost.Build();

        // WinUI XAML failures otherwise surface only as a native stowed-exception crash.
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waverider");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "crash.log"),
                    $"{DateTime.Now:O}{Environment.NewLine}{e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var themeService = (ThemeService)AppHost.Services.GetService(typeof(ThemeService))!;
        await themeService.LoadAsync();

        _window = new MainWindow();
        _window.Activate();
    }
}
