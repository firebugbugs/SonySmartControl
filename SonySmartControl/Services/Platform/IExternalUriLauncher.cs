namespace SonySmartControl.Services.Platform;

/// <summary>使用系统默认程序打开 http(s) 等 URI。</summary>
public interface IExternalUriLauncher
{
    void OpenUri(string uri);
}
