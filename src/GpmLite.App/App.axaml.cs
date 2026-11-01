using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GpmLite.App.ViewModels;
using GpmLite.App.Views;
using GpmLite.Core.Services;
using GpmLite.Core.Storage;

namespace GpmLite.App;

public partial class App : Application
{
    private LocalApiServer? _api;
    private ProfileService? _service;
    private System.Runtime.InteropServices.PosixSignalRegistration[] _signals = [];

    private void OnSignal(System.Runtime.InteropServices.PosixSignalContext ctx)
    {
        ctx.Cancel = true; // we exit ourselves after cleanup
        OnShutdownRequested(this, new ShutdownRequestedEventArgs());
        Environment.Exit(0);
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new AppPaths();
            var settings = AppSettings.Load(paths.SettingsFile);
            var repo = new Repository(new Database(paths.DatabaseFile));
            _service = new ProfileService(paths, repo, settings);
            // Append diagnostics (launch errors, etc.) to <data dir>/gpmlite.log so issues are inspectable.
            _service.Log += (id, msg) =>
            {
                try { System.IO.File.AppendAllText(paths.LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{id}] {msg}{Environment.NewLine}"); }
                catch { /* logging must never crash the app */ }
            };

            string? apiStatus = null;
            if (settings.LocalApiEnabled)
            {
                try
                {
                    _api = new LocalApiServer(_service);
                    _api.Start(settings.LocalApiPort);
                    apiStatus = _api.Prefix;
                }
                catch (Exception e)
                {
                    apiStatus = $"lỗi: {e.Message}";
                    _api = null;
                }
            }

            var vm = new MainWindowViewModel(_service, apiStatus);
            var window = new MainWindow { DataContext = vm };
            Dialogs.Owner = window;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += OnShutdownRequested;
            // `kill`/Ctrl+C would otherwise leave browsers running without their proxy relay.
            _signals =
            [
                System.Runtime.InteropServices.PosixSignalRegistration.Create(
                    System.Runtime.InteropServices.PosixSignal.SIGTERM, OnSignal),
                System.Runtime.InteropServices.PosixSignalRegistration.Create(
                    System.Runtime.InteropServices.PosixSignal.SIGINT, OnSignal),
            ];
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // Close browsers gracefully so cookies/sessions are flushed to the profile dirs.
        // Run on the thread pool: blocking the UI thread on tasks that resume on it would deadlock.
        var service = _service;
        var api = _api;
        try { Task.Run(() => service?.StopAllAsync() ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(10)); } catch { /* best effort */ }
        try { Task.Run(async () => { if (api is not null) await api.DisposeAsync(); }).Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
