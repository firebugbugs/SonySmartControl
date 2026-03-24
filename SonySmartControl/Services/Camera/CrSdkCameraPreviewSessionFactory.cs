namespace SonySmartControl.Services.Camera;

/// <summary>构造 <see cref="CrSdkCameraPreviewSession"/>。</summary>
public sealed class CrSdkCameraPreviewSessionFactory : ICameraPreviewSessionFactory
{
    public ICameraPreviewSession Create() => new CrSdkCameraPreviewSession();
}
