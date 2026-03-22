using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SonySmartControl;

sealed class Program
{
    /// <summary>让加载 SonyCrBridge / Cr_Core 时在 exe 目录与 CrAdapter 子目录中解析依赖。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            var dir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(dir))
                dir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";

            if (!string.IsNullOrEmpty(dir))
            {
                var crAdapter = Path.Combine(dir, "CrAdapter");
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (Directory.Exists(crAdapter))
                    Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + crAdapter + Path.PathSeparator + pathEnv);
                else
                    Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + pathEnv);

                try
                {
                    SetDllDirectory(dir);
                }
                catch
                {
                    // ignore
                }
            }
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}