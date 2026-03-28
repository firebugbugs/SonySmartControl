using Microsoft.Extensions.DependencyInjection;
using SonySmartControl.Services.Camera;
using SonySmartControl.Services.Logging;
using SonySmartControl.Services.Platform;
using SonySmartControl.Services.Settings;
using SonySmartControl.ViewModels;

namespace SonySmartControl.Services;

/// <summary>应用内服务注册（Microsoft.Extensions.DependencyInjection）。</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSonySmartControlServices(this IServiceCollection services)
    {
        services.AddSingleton<ITopLevelProvider, TopLevelProvider>();
        services.AddSingleton<IMainWindowShellService, MainWindowShellService>();
        services.AddSingleton<IExternalUriLauncher, ProcessExternalUriLauncher>();
        services.AddSingleton<IFolderPickerService, AvaloniaFolderPickerService>();
        services.AddSingleton<ICameraSettingsProfilesStore, SqliteCameraSettingsProfilesStore>();
        // 兼容旧接口：仍保留注册，但主流程已迁移到 SQLite 多配置。
        services.AddSingleton<IUserCameraSettingsService, UserCameraSettingsService>();
        services.AddSingleton<ICameraPreviewSessionFactory, CrSdkCameraPreviewSessionFactory>();
        services.AddSingleton<ISdCardMediaFormatService, CrSdkSdCardMediaFormatService>();
        services.AddSingleton<MainWindowCameraOperations>(sp =>
            new MainWindowCameraOperations(
                sp.GetRequiredService<ICameraPreviewSessionFactory>(),
                sp.GetRequiredService<ISdCardMediaFormatService>(),
                () => sp.GetRequiredService<MainWindowViewModel>()));
        services.AddSingleton<ICrSdkShootingWriteService, CrSdkShootingWriteService>();
        services.AddSingleton<IAppLogService, AppLogService>();
        services.AddTransient<LogHistoryViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}
