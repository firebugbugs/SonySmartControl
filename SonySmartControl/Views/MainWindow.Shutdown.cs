using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SonySmartControl.Helpers;
using SonySmartControl.ViewModels;

namespace SonySmartControl.Views;

public partial class MainWindow
{
    /// <summary>取消首次关闭、异步释放 ViewModel，再允许真正 Close；详见 ShutdownTrace。</summary>
    private async void OnClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window)
            return;
        if (_allowWindowClose)
            return;

        e.Cancel = true;
        ShutdownTrace.EnableForShutdown();
        ShutdownTrace.Write("MainWindow.Closing: begin (cancel close, start cleanup)");
        try
        {
            if (window.DataContext is MainWindowViewModel vm)
            {
                ShutdownTrace.Write("MainWindow.Closing: vm.DisposeAsync start");
                // 必须在 UI 同步上下文上执行释放：Observable / RelayCommand 会触达 Avalonia 控件（VerifyAccess）。
                // 相机会话内的 TryDisconnect 等已在 CrSdkCameraPreviewSession 中用 Task.Run 卸载，此处勿再 Task.Run。
                var disposeTask = vm.DisposeAsync().AsTask();
                var completed = await Task
                    .WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)))
                    .ConfigureAwait(true);
                if (!ReferenceEquals(completed, disposeTask))
                {
                    ShutdownTrace.Write("MainWindow.Closing: vm.DisposeAsync TIMEOUT (continue shutdown)");
                }
                else
                {
                    try
                    {
                        await disposeTask.ConfigureAwait(true);
                        ShutdownTrace.Write("MainWindow.Closing: vm.DisposeAsync completed");
                    }
                    catch (Exception ex)
                    {
                        ShutdownTrace.Write("MainWindow.Closing: vm.DisposeAsync faulted", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShutdownTrace.Write("MainWindow.Closing: unexpected exception (ignored)", ex);
        }
        finally
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _allowWindowClose = true;
                    ShutdownTrace.Write("MainWindow.Closing: allow close=true, calling window.Close()");
                    window.Close();

                    try
                    {
                        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            ShutdownTrace.Write("MainWindow.Closing: calling desktop.Shutdown()");
                            desktop.Shutdown();
                        }
                    }
                    catch (Exception ex)
                    {
                        ShutdownTrace.Write("MainWindow.Closing: desktop.Shutdown() threw (ignored)", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                ShutdownTrace.Write("MainWindow.Closing: finally UIThread.InvokeAsync failed", ex);
            }
        }
    }

    /// <summary>主窗关闭后独立线程等待 3s 再强杀进程，避免线程池在关机阶段不调度导致挂起。</summary>
    private static void OnWindowClosedScheduleHardExit(object? sender, EventArgs e)
    {
        var t = new Thread(HardExitWatchdogThreadProc)
        {
            Name = "HardExitWatchdog",
            IsBackground = false
        };
        t.Start();
    }

    private static void HardExitWatchdogThreadProc()
    {
        try
        {
            SleepNative(3000);
        }
        catch
        {
        }

        try
        {
            Process.GetCurrentProcess().Kill(entireProcessTree: true);
        }
        catch
        {
            try
            {
                Environment.Exit(0);
            }
            catch
            {
            }
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void Sleep(uint dwMilliseconds);

    private static void SleepNative(int milliseconds) => Sleep((uint)milliseconds);
}
