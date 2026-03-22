using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace SonySmartControl.Controls;

/// <summary>
/// 原图回看：对位图做真正的 Scale 变换；滚轮缩放、拖拽平移。
/// 初始在控件有有效尺寸后整图「适应窗口」居中显示。
/// </summary>
public partial class PhotoPanZoomViewer : UserControl
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<PhotoPanZoomViewer, IImage?>(nameof(Source));

    private ScaleTransform? _scaleT;
    private TranslateTransform? _translateT;
    private double _scale = 1;
    private double _tx;
    private double _ty;
    private Point _lastPointer;
    private bool _dragging;
    private bool _needsInitialFit = true;
    /// <summary>用户是否已滚轮缩放或拖拽；未操作时窗口尺寸变化会重新「适应」画布。</summary>
    private bool _userAdjustedView;

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public PhotoPanZoomViewer()
    {
        InitializeComponent();
        Loaded += (_, _) => TryInitialFit();
    }

    static PhotoPanZoomViewer()
    {
        SourceProperty.Changed.AddClassHandler<PhotoPanZoomViewer>((o, e) => o.OnSourceChanged(e));
    }

    private void OnSourceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        PART_Image.Source = e.NewValue as IImage;
        if (e.NewValue == null)
        {
            _needsInitialFit = false;
            _userAdjustedView = false;
            if (_scaleT != null)
            {
                _scaleT.ScaleX = _scaleT.ScaleY = 1;
                _translateT!.X = _translateT.Y = 0;
            }

            return;
        }

        _scale = 1;
        _tx = _ty = 0;
        _userAdjustedView = false;
        _needsInitialFit = true;
        EnsureTransforms();
        if (PART_Image.Source is Bitmap bmp)
        {
            PART_Image.Width = bmp.PixelSize.Width;
            PART_Image.Height = bmp.PixelSize.Height;
        }

        TryInitialFit();
        if (Bounds.Width <= 1 || Bounds.Height <= 1)
            Dispatcher.UIThread.Post(TryInitialFit, DispatcherPriority.Loaded);
    }

    private void PhotoPanZoomViewer_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
            return;

        if (_needsInitialFit)
        {
            TryInitialFit();
            return;
        }

        if (PART_Image.Source is not Bitmap bmp)
            return;

        var vw = Bounds.Width;
        var vh = Bounds.Height;
        var iw = bmp.PixelSize.Width;
        var ih = bmp.PixelSize.Height;
        if (iw <= 0 || ih <= 0)
            return;

        EnsureTransforms();
        if (!_userAdjustedView)
        {
            _scale = Math.Min(vw / iw, vh / ih);
            _tx = (vw - iw * _scale) / 2;
            _ty = (vh - ih * _scale) / 2;
        }
        else
            ClampTranslate(iw, ih, vw, vh);

        ApplyTransform();
    }

    private void EnsureTransforms()
    {
        if (_scaleT != null)
            return;
        _scaleT = new ScaleTransform(1, 1);
        _translateT = new TranslateTransform();
        PART_Image.RenderTransform = new TransformGroup
        {
            Children = { _scaleT, _translateT },
        };
    }

    /// <summary>
    /// 将图像像素 (ix,iy) 映射到控件坐标：先 Scale(s) 再 Translate(tx,ty)，即 (ix*s+tx, iy*s+ty)。
    /// </summary>
    private void ApplyTransform()
    {
        if (PART_Image.Source is not Bitmap bmp || _scaleT == null || _translateT == null)
            return;

        var iw = bmp.PixelSize.Width;
        var ih = bmp.PixelSize.Height;
        if (iw <= 0 || ih <= 0)
            return;

        PART_Image.Width = iw;
        PART_Image.Height = ih;
        _scaleT.ScaleX = _scaleT.ScaleY = _scale;
        _translateT.X = _tx;
        _translateT.Y = _ty;
    }

    private void TryInitialFit()
    {
        if (!_needsInitialFit || PART_Image.Source is not Bitmap bmp)
            return;

        var vw = Bounds.Width;
        var vh = Bounds.Height;
        if (vw <= 1 || vh <= 1)
            return;

        var iw = bmp.PixelSize.Width;
        var ih = bmp.PixelSize.Height;
        if (iw <= 0 || ih <= 0)
            return;

        EnsureTransforms();
        _scale = Math.Min(vw / iw, vh / ih);
        _tx = (vw - iw * _scale) / 2;
        _ty = (vh - ih * _scale) / 2;
        ApplyTransform();
        _needsInitialFit = false;
    }

    private void Viewport_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (PART_Image.Source is not Bitmap bmp || _scaleT == null || _translateT == null)
            return;

        var vw = Bounds.Width;
        var vh = Bounds.Height;
        var pos = e.GetPosition(this);
        var d = e.Delta.Y;
        if (Math.Abs(d) < 1e-6)
            return;

        var iw = bmp.PixelSize.Width;
        var ih = bmp.PixelSize.Height;

        // 以指针为锚点缩放：保持该像素在屏幕上的位置
        var factor = d > 0 ? 1.12 : 1.0 / 1.12;
        var newScale = Math.Clamp(_scale * factor, 0.02, 80.0);

        var mx = (pos.X - _tx) / _scale;
        var my = (pos.Y - _ty) / _scale;
        _tx = pos.X - mx * newScale;
        _ty = pos.Y - my * newScale;
        _scale = newScale;

        ClampTranslate(iw, ih, vw, vh);
        ApplyTransform();
        _needsInitialFit = false;
        _userAdjustedView = true;
        e.Handled = true;
    }

    private void ClampTranslate(double iw, double ih, double vw, double vh)
    {
        var w = iw * _scale;
        var h = ih * _scale;
        if (w <= vw)
            _tx = (vw - w) / 2;
        else
            _tx = Math.Clamp(_tx, vw - w, 0);

        if (h <= vh)
            _ty = (vh - h) / 2;
        else
            _ty = Math.Clamp(_ty, vh - h, 0);
    }

    private void Viewport_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void Viewport_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || PART_Image.Source is not Bitmap bmp || _scaleT == null)
            return;

        var pos = e.GetPosition(this);
        var dx = pos.X - _lastPointer.X;
        var dy = pos.Y - _lastPointer.Y;
        _lastPointer = pos;
        _tx += dx;
        _ty += dy;

        var iw = bmp.PixelSize.Width;
        var ih = bmp.PixelSize.Height;
        ClampTranslate(iw, ih, Bounds.Width, Bounds.Height);
        ApplyTransform();
        _needsInitialFit = false;
        _userAdjustedView = true;
        e.Handled = true;
    }

    private void Viewport_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndDrag(e.Pointer);
    }

    private void Viewport_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndDrag(e.Pointer);
    }

    private void EndDrag(IPointer pointer)
    {
        if (!_dragging)
            return;
        _dragging = false;
        pointer.Capture(null);
    }
}
