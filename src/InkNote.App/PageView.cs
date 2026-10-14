using System.Numerics;
using InkNote.Core.Ink;
using InkNote.Core.Pdfium;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Color = Windows.UI.Color;

namespace InkNote.App;

/// <summary>
/// PDF 한 쪽. 화면에 보일 때만 Win2D 캔버스를 붙여(<see cref="Realize"/>) 메모리를 아낍니다.
/// 그리는 순서: PDF 비트맵 → 형광펜(최소값 블렌드: 글자가 묻히지 않음) → 펜 → 지금 긋는 획.
/// 좌표는 PDF 포인트(1/72인치) 기준이고, 화면 크기는 1포인트 = 4/3 DIP(100% 배율에서 실제 크기)입니다.
/// </summary>
public sealed class PageView : Grid
{
    public const float DipPerPoint = 96f / 72f;

    // 캔버스 한 변의 최대 픽셀. 크게 확대해도 GPU 메모리가 폭주하지 않게 막습니다.
    private const float MaxSurfacePixels = 4096f;

    private static readonly CanvasStrokeStyle RoundStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    private readonly PdfDocument _pdf;
    private readonly InkDocument _ink;
    private readonly ToolState _tools;
    private readonly Func<float> _zoom;
    private readonly float _pageW, _pageH; // 포인트

    private CanvasControl? _canvas;
    private CanvasBitmap? _pdfBitmap;
    private (byte[] Pixels, int W, int H)? _pendingPixels;
    private float _renderedScale;
    private int _renderVersion;

    private CanvasRenderTarget? _penCache, _highlightCache;
    private bool _cacheDirty = true;

    private uint? _activePointer;
    private bool _erasing;
    private InkStroke? _live;

    public PageView(int index, PdfDocument pdf, InkDocument ink, ToolState tools, Func<float> zoom)
    {
        PageIndex = index;
        _pdf = pdf;
        _ink = ink;
        _tools = tools;
        _zoom = zoom;
        (_pageW, _pageH) = pdf.GetPageSize(index);
        Width = _pageW * DipPerPoint;
        Height = _pageH * DipPerPoint;
        Background = new SolidColorBrush(Colors.White);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
        BorderThickness = new Thickness(1);
        IsRightTapEnabled = false;
        IsHoldingEnabled = false;
        IsDoubleTapEnabled = false;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerEnded;
        PointerCanceled += OnPointerEnded;
        PointerCaptureLost += OnPointerEnded;
        PointerEntered += OnPointerHover;
        PointerExited += OnPointerExited;
    }

    public int PageIndex { get; }
    public bool IsRealized => _canvas != null;

    /// <summary>펜이 페이지 위에 떠 있는지 알림. 떠 있는 동안 터치 스크롤을 꺼서 손바닥이 화면을 밀지 않게 합니다.</summary>
    public event Action<bool>? PenHoverChanged;

    public void Realize()
    {
        if (_canvas != null) return;
        _canvas = new CanvasControl { IsHitTestVisible = false, ClearColor = Colors.White };
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);
        UpdateRenderScale();
    }

    public void Unrealize()
    {
        if (_canvas == null) return;
        _renderVersion++;
        _canvas.CreateResources -= OnCreateResources;
        _canvas.Draw -= OnDraw;
        _canvas.RemoveFromVisualTree();
        Children.Remove(_canvas);
        _canvas = null;
        DisposeSurfaces();
        _pendingPixels = null;
        _renderedScale = 0;
    }

    /// <summary>확대가 끝났을 때 호출. 배율에 맞춰 PDF를 다시 렌더링하고 캔버스 해상도를 올립니다.</summary>
    public void UpdateRenderScale()
    {
        if (_canvas == null || XamlRoot == null) return;
        float raster = (float)XamlRoot.RasterizationScale;
        float longestDip = MathF.Max((float)Width, (float)Height);
        float scale = MathF.Min(MathF.Max(_zoom(), 0.25f), MaxSurfacePixels / (longestDip * raster));
        if (_renderedScale > 0 && MathF.Abs(scale - _renderedScale) / _renderedScale < 0.1f) return;
        _renderedScale = scale;
        _canvas.DpiScale = scale;
        _cacheDirty = true;
        RenderPdfAsync(raster * scale);
    }

    public void InvalidateInk()
    {
        _cacheDirty = true;
        _canvas?.Invalidate();
    }

    private async void RenderPdfAsync(float pixelsPerDip)
    {
        int version = ++_renderVersion;
        int w = Math.Max(1, (int)MathF.Round((float)Width * pixelsPerDip));
        int h = Math.Max(1, (int)MathF.Round((float)Height * pixelsPerDip));
        byte[] pixels;
        try
        {
            pixels = await Task.Run(() => _pdf.RenderPage(PageIndex, w, h));
        }
        catch (ObjectDisposedException)
        {
            return; // 그사이 다른 문서를 열었음
        }
        if (version != _renderVersion || _canvas == null) return;
        _pendingPixels = (pixels, w, h);
        _canvas.Invalidate();
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // GPU 장치가 바뀌면(드라이버 재시작 등) 이전 GPU 자원은 못 쓰므로 PDF부터 다시 렌더링합니다.
        if (args.Reason == CanvasCreateResourcesReason.NewDevice)
        {
            DisposeSurfaces();
            _renderedScale = 0;
            UpdateRenderScale();
        }
        _cacheDirty = true;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var pageRect = new Rect(0, 0, Width, Height);

        if (_pendingPixels is { } p)
        {
            _pdfBitmap?.Dispose();
            _pdfBitmap = CanvasBitmap.CreateFromBytes(sender, p.Pixels, p.W, p.H, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _pendingPixels = null;
        }
        if (_pdfBitmap != null) ds.DrawImage(_pdfBitmap, pageRect);

        if (_cacheDirty || _penCache == null || _penCache.Dpi != sender.Dpi) RebuildCaches(sender);

        if (_highlightCache != null)
        {
            ds.Blend = CanvasBlend.Min;
            ds.DrawImage(_highlightCache);
            ds.Blend = CanvasBlend.SourceOver;
        }
        ds.DrawImage(_penCache!);

        if (_live != null && !_erasing)
        {
            ds.Transform = Matrix3x2.CreateScale(DipPerPoint);
            if (_live.Tool == InkTool.Highlighter) ds.Blend = CanvasBlend.Min;
            DrawStroke(ds, _live);
            ds.Blend = CanvasBlend.SourceOver;
            ds.Transform = Matrix3x2.Identity;
        }
    }

    private void RebuildCaches(CanvasControl sender)
    {
        _cacheDirty = false;
        var strokes = _ink.GetStrokes(PageIndex);
        float w = (float)Width, h = (float)Height;

        _penCache?.Dispose();
        _penCache = new CanvasRenderTarget(sender, w, h);
        using (var ds = _penCache.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(DipPerPoint);
            foreach (var s in strokes)
                if (s.Tool == InkTool.Pen) DrawStroke(ds, s);
        }

        _highlightCache?.Dispose();
        _highlightCache = null;
        if (strokes.Any(s => s.Tool == InkTool.Highlighter))
        {
            // 흰 바탕에 최소값 블렌드로 그리고, 페이지 위에도 최소값 블렌드로 얹습니다.
            // 흰색은 페이지를 바꾸지 않고, 겹쳐 칠해도 더 진해지지 않습니다(실제 형광펜처럼).
            _highlightCache = new CanvasRenderTarget(sender, w, h);
            using var ds = _highlightCache.CreateDrawingSession();
            ds.Clear(Colors.White);
            ds.Transform = Matrix3x2.CreateScale(DipPerPoint);
            ds.Blend = CanvasBlend.Min;
            foreach (var s in strokes)
                if (s.Tool == InkTool.Highlighter) DrawStroke(ds, s);
        }
    }

    private static void DrawStroke(CanvasDrawingSession ds, InkStroke s)
    {
        var color = ScreenColor(s);
        var pts = s.Points;
        if (pts.Count == 1)
        {
            ds.FillCircle(pts[0].X, pts[0].Y, pts[0].Width / 2, color);
            return;
        }
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            ds.DrawLine(a.X, a.Y, b.X, b.Y, color, (a.Width + b.Width) / 2, RoundStroke);
        }
    }

    /// <summary>형광펜은 흰 바탕에 투명도를 미리 섞은 불투명 색(최소값 블렌드용). PDF에는 곱하기 블렌드로 저장됩니다.</summary>
    private static Color ScreenColor(InkStroke s)
    {
        if (s.Tool == InkTool.Pen) return Color.FromArgb(255, s.Color.R, s.Color.G, s.Color.B);
        byte Mix(byte c) => (byte)Math.Round(255 - (255 - c) * s.Opacity);
        return Color.FromArgb(255, Mix(s.Color.R), Mix(s.Color.G), Mix(s.Color.B));
    }

    // ───── 입력 ─────

    private void OnPointerHover(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) PenHoverChanged?.Invoke(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen && _activePointer == null) PenHoverChanged?.Invoke(false);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 손가락은 그리지 않고 스크롤·확대에 씁니다(손바닥 무시).
        var type = e.Pointer.PointerDeviceType;
        if (type == PointerDeviceType.Touch || _activePointer != null || _canvas == null) return;

        var pt = e.GetCurrentPoint(this);
        var props = pt.Properties;
        bool mouseRight = type == PointerDeviceType.Mouse && props.IsRightButtonPressed;
        if (type == PointerDeviceType.Mouse && !props.IsLeftButtonPressed && !mouseRight) return;

        // 펜 뒤쪽 지우개, 펜 옆 버튼, 마우스 오른쪽 버튼은 언제나 지우개.
        _erasing = _tools.Kind == ToolKind.Eraser || props.IsEraser || props.IsInverted || props.IsBarrelButtonPressed || mouseRight;
        if (!CapturePointer(e.Pointer)) return;
        _activePointer = e.Pointer.PointerId;
        if (type == PointerDeviceType.Pen) PenHoverChanged?.Invoke(true);

        if (_erasing)
        {
            EraseAt(pt.Position);
        }
        else
        {
            _live = new InkStroke(_tools.InkTool, _tools.CurrentColor);
            AddPoint(pt);
        }
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _activePointer)
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) PenHoverChanged?.Invoke(true);
            return;
        }
        // 펜은 화면 주사율보다 촘촘히 보고하므로 중간 점까지 모두 씁니다(오래된 것부터).
        var points = e.GetIntermediatePoints(this);
        for (int i = points.Count - 1; i >= 0; i--)
        {
            if (_erasing) EraseAt(points[i].Position);
            else AddPoint(points[i]);
        }
        if (!_erasing) _canvas?.Invalidate();
        e.Handled = true;
    }

    private void OnPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _activePointer) return;
        _activePointer = null;
        ReleasePointerCapture(e.Pointer);
        var stroke = _live;
        _live = null;
        _erasing = false;
        if (stroke != null) _ink.AddStroke(PageIndex, stroke); // PageChanged → InvalidateInk
        _canvas?.Invalidate();
        e.Handled = true;
    }

    private void AddPoint(PointerPoint pt)
    {
        if (_live == null) return;
        float x = (float)pt.Position.X / DipPerPoint;
        float y = (float)pt.Position.Y / DipPerPoint;
        if (_live.Points.Count > 0)
        {
            var last = _live.Points[^1];
            float dx = x - last.X, dy = y - last.Y;
            if (dx * dx + dy * dy < 0.04f) return; // 0.2pt 미만 이동은 버림
        }
        float pressure = pt.PointerDeviceType == PointerDeviceType.Pen ? pt.Properties.Pressure : 0.5f;
        _live.Points.Add(new InkPoint(x, y, InkStroke.WidthFor(_live.Tool, _tools.CurrentBaseWidth, pressure)));
    }

    private void EraseAt(Point position)
    {
        float radius = ToolState.EraserRadiusDip / MathF.Max(_zoom(), 0.1f) / DipPerPoint;
        _ink.EraseAt(PageIndex, (float)position.X / DipPerPoint, (float)position.Y / DipPerPoint, radius);
    }

    private void DisposeSurfaces()
    {
        _pdfBitmap?.Dispose();
        _pdfBitmap = null;
        _penCache?.Dispose();
        _penCache = null;
        _highlightCache?.Dispose();
        _highlightCache = null;
        _cacheDirty = true;
    }
}
