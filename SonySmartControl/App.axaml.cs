using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SonySmartControl.Services;
using SonySmartControl.ViewModels;
using SonySmartControl.Views;
using System.Linq;

namespace SonySmartControl;

public partial class App : Application
{
    /// <summary>应用内服务根容器（构造函数注入解析入口）。</summary>
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            // 关键：主窗口关闭时直接退出应用（而不是等待“最后一个窗口关闭”）。
            // 否则日志窗/设备搜索窗等工具窗口仍开着时，会出现“主窗已关但进程还在”。
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var services = new ServiceCollection();
            services.AddSonySmartControlServices();
            Services = services.BuildServiceProvider();

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
            // 注意：部分机型/镜头组合在 SonyCr_Release 期间可能发生 native 访问冲突（0xC0000005）。
            // 当前策略：会话仅做 Disconnect，不主动调用 SonyCr_Release，避免「断开」时闪退。
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}