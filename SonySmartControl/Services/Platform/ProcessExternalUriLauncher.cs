using System.Diagnostics;

namespace SonySmartControl.Services.Platform;

public sealed class ProcessExternalUriLauncher : IExternalUriLauncher
{
    public void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
        }
        catch
        {
            // 忽略
        }
    }
}
