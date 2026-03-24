using SonySmartControl.Interop;

namespace SonySmartControl.Services.Camera;

/// <summary>封装 <see cref="CrSdkCommandIds.MediaFormat"/> 的 SLOT1/SLOT2 调用，避免 ViewModel 直接依赖 P/Invoke。</summary>
public sealed class CrSdkSdCardMediaFormatService : ISdCardMediaFormatService
{
    public Task<SdCardFormatResult> FormatAllSlotsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var errors = new List<string>();
                var okCount = 0;

                void TryOne(CrSdkCommandParam slotParam, string slotName)
                {
                    try
                    {
                        SonyCrSdk.SendCommand(CrSdkCommandIds.MediaFormat, slotParam);
                        okCount++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{slotName} 失败：{ex.Message}");
                    }
                }

                TryOne(CrSdkCommandParam.Up, "SLOT1");
                TryOne(CrSdkCommandParam.Down, "SLOT2");

                if (okCount > 0)
                    return new SdCardFormatResult(errors.Count > 0 ? string.Join("；", errors) : null);

                throw new InvalidOperationException(string.Join("；", errors));
            },
            cancellationToken);

}
