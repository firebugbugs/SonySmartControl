using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace SonySmartControl.Helpers;

/// <summary>
/// Windows 回退解码：通过 Shell 缩略图管线读取 HEIF/HEIC 等系统可识别格式。
/// 仅用于主解码失败时兜底，避免底部胶片与回看大图空白。
/// </summary>
internal static class WindowsShellImageDecoder
{
    public static Bitmap? TryDecode(string path, int maxEdge)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        maxEdge = Math.Clamp(maxEdge, 64, 4096);

        IShellItemImageFactory? factory = null;
        nint hBitmap = nint.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, nint.Zero, in iid, out var ppv);
            if (hr != 0 || ppv == nint.Zero)
                return null;

            factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
            Marshal.Release(ppv);

            var size = new NativeSize { cx = maxEdge, cy = maxEdge };
            hr = factory.GetImage(
                size,
                SIIGBF.SIIGBF_BIGGERSIZEOK | SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_THUMBNAILONLY,
                out hBitmap);
            if (hr != 0 || hBitmap == nint.Zero)
                return null;

            using var bmp = System.Drawing.Image.FromHbitmap(hBitmap);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != nint.Zero)
                DeleteObject(hBitmap);
            if (factory != null)
                Marshal.ReleaseComObject(factory);
        }
    }

    [Flags]
    private enum SIIGBF : uint
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10,
        SIIGBF_CROPTOSQUARE = 0x20,
        SIIGBF_WIDETHUMBNAILS = 0x40,
        SIIGBF_ICONBACKGROUND = 0x80,
        SIIGBF_SCALEUP = 0x100,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        int GetImage(NativeSize size, SIIGBF flags, out nint phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        nint pbc,
        in Guid riid,
        out nint ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint hObject);
}

