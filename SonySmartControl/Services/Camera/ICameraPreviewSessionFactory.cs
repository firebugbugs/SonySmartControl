namespace SonySmartControl.Services.Camera;

/// <summary>创建新的相机预览会话实例（每次连接使用独立会话）。</summary>
public interface ICameraPreviewSessionFactory
{
    ICameraPreviewSession Create();
}
