using System.Windows;
using System.Windows.Threading;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Epg;
using IPTVPlayer.Services.Net;
using IPTVPlayer.Services.Parsing;
using IPTVPlayer.Services.Playback;
using IPTVPlayer.Services.Security;
using IPTVPlayer.Services.UI;
using IPTVPlayer.Services.Xtream;
using IPTVPlayer.ViewModels;
using IPTVPlayer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace IPTVPlayer;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        Log.Initialize(paths.LogsDirectory);
        Log.Info("Arranque.");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            LibVLCSharp.Shared.Core.Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao carregar o LibVLC.", ex);
            Fatal("Não foi possível carregar o motor de vídeo (LibVLC). Reinstala a aplicação.");
            return;
        }

        try
        {
            _services = ConfigureServices(paths);
            _services.GetRequiredService<AppDatabase>().Initialize();
            if (_services.GetRequiredService<StreamUrlCipher>().KeyWasReset)
                _services.GetRequiredService<ChannelRepository>().DeleteAll();
            var settings = _services.GetRequiredService<SettingsRepository>();
            var player = _services.GetRequiredService<PlayerService>();
            player.NetworkCachingMs = (int)Math.Clamp(settings.GetInt64(SettingsRepository.NetworkCachingMs) ?? 1500, 300, 20000);
            player.HardwareDecoding = settings.Get(SettingsRepository.HardwareDecoding) != "0";

            _services.GetRequiredService<RecordingService>().Start();

            var imageCache = _services.GetRequiredService<ImageDiskCache>();
            ImageLoader.Initialize(_services.GetRequiredService<HttpClients>().Logos, imageCache);
            _ = imageCache.TrimAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao iniciar os serviços.", ex);
            Fatal($"Não foi possível abrir a base de dados em {paths.DataDirectory}.\n\n{ex.Message}");
            return;
        }

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // Preferências de faixas e comando.
        var prefs = _services.GetRequiredService<SettingsRepository>();
        var playerVm = _services.GetRequiredService<PlayerViewModel>();
        playerVm.PreferredAudioLanguage = prefs.Get(SettingsRepository.PreferredAudio);
        playerVm.PreferredSubtitleLanguage = prefs.Get(SettingsRepository.PreferredSubtitle);
        _services.GetRequiredService<GamepadService>().Enabled = prefs.Get(SettingsRepository.GamepadEnabled) != "0";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Erro ao terminar.", ex);
        }
        Log.Info("Fim.");
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var services = new ServiceCollection();

        services.AddSingleton(paths);
        services.AddSingleton<AppDatabase>();
        services.AddSingleton<PlaylistRepository>();
        services.AddSingleton<ChannelRepository>();
        services.AddSingleton<FavoritesRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<EpgRepository>();
        services.AddSingleton<VodRepository>();
        services.AddSingleton<WatchProgressRepository>();
        services.AddSingleton<RecordingRepository>();

        services.AddSingleton<CredentialProtector>();
        services.AddSingleton<StreamUrlCipher>();
        services.AddSingleton<HttpClients>();
        services.AddSingleton<ImageDiskCache>();
        services.AddSingleton<M3uParser>();
        services.AddSingleton<XtreamClient>();
        services.AddSingleton<PlaylistService>();
        services.AddSingleton<EpgService>();
        services.AddSingleton<VodService>();
        services.AddSingleton<ParentalControlService>();
        services.AddSingleton<LibVlcHost>();
        services.AddSingleton<PlayerService>();
        services.AddSingleton<RecordingService>();
        services.AddSingleton<IDialogService, DialogService>();

        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<VodBrowserViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<AddPlaylistViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<RecordingsViewModel>();
        services.AddSingleton<GamepadService>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void Fatal(string message)
    {
        MessageBox.Show(message, "PepinoTv", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Exceção não tratada na UI.", e.Exception);
        e.Handled = true;
        var message = e.Exception is UserFacingException ufe
            ? ufe.Message
            : "Ocorreu um erro inesperado. A aplicação vai continuar a funcionar.\n\nDetalhes no ficheiro de log.";
        if (MainWindow is { IsLoaded: true } owner)
            MessageBox.Show(owner, message, "PepinoTv", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, "PepinoTv", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Exceção numa tarefa não observada.", e.Exception);
        e.SetObserved();
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("Exceção fatal.", e.ExceptionObject as Exception);
    }
}
