using Microsoft.Extensions.DependencyInjection;
using SonySmartControl.Services.Camera;
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
        services.AddSingleton<IFolderPickerService, AvaloniaFolderPickerService>();
        services.AddSingleton<IUserCameraSettingsService, UserCameraSettingsService>();
        services.AddSingleton<ICameraPreviewSessionFactory, CrSdkCameraPreviewSessionFactory>();
        services.AddSingleton<ISdCardMediaFormatService, CrSdkSdCardMediaFormatService>();
        services.AddSingleton<ICrSdkShootingWriteService, CrSdkShootingWriteService>();
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}
