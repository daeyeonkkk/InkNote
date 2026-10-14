namespace InkNote.Core.Ink;

public enum InkTool
{
    Pen,
    Highlighter,
}

/// <summary>sRGB 색. 투명도는 도구(형광펜)가 정합니다.</summary>
public readonly record struct InkColor(byte R, byte G, byte B)
{
    public static readonly InkColor Black = new(0x11, 0x11, 0x11);
    public static readonly InkColor Blue = new(0x1A, 0x5F, 0xD6);
    public static readonly InkColor Red = new(0xD6, 0x2A, 0x2A);
    public static readonly InkColor Yellow = new(0xFF, 0xE0, 0x3D);
    public static readonly InkColor Green = new(0x5C, 0xD6, 0x5C);
}

/// <summary>
/// 페이지 좌표의 한 점. 단위는 PDF 포인트(1/72인치), 원점은 화면에 보이는 페이지의 왼쪽 위,
/// y는 아래로 증가합니다. Width는 그 점에서의 선 굵기(포인트)입니다.
/// </summary>
public readonly record struct InkPoint(float X, float Y, float Width);

public readonly record struct InkRect(float Left, float Top, float Right, float Bottom)
{
    public bool Intersects(InkRect o) => Left <= o.Right && o.Left <= Right && Top <= o.Bottom && o.Top <= Bottom;
}

public sealed class InkStroke
{
    public const float HighlighterOpacity = 0.35f;

    public InkStroke(InkTool tool, InkColor color, IEnumerable<InkPoint>? points = null)
    {
        Tool = tool;
        Color = color;
        if (points != null) Points.AddRange(points);
    }

    public InkTool Tool { get; }
    public InkColor Color { get; }
    public List<InkPoint> Points { get; } = new();

    public float Opacity => Tool == InkTool.Highlighter ? HighlighterOpacity : 1f;

    /// <summary>선 굵기 절반까지 포함한 경계 상자.</summary>
    public InkRect Bounds
    {
        get
        {
            if (Points.Count == 0) return default;
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var p in Points)
            {
                float h = p.Width / 2;
                l = Math.Min(l, p.X - h); t = Math.Min(t, p.Y - h);
                r = Math.Max(r, p.X + h); b = Math.Max(b, p.Y + h);
            }
            return new InkRect(l, t, r, b);
        }
    }

    /// <summary>점 (x, y)에서 반지름 radius 원이 이 획에 닿는지.</summary>
    public bool HitTest(float x, float y, float radius)
    {
        if (Points.Count == 0) return false;
        var probe = new InkRect(x - radius, y - radius, x + radius, y + radius);
        if (!Bounds.Intersects(probe)) return false;
        if (Points.Count == 1)
        {
            var p = Points[0];
            return Distance(x, y, p.X, p.Y) <= radius + p.Width / 2;
        }
        for (int i = 1; i < Points.Count; i++)
        {
            var a = Points[i - 1];
            var b = Points[i];
            float reach = radius + Math.Max(a.Width, b.Width) / 2;
            if (DistanceToSegment(x, y, a.X, a.Y, b.X, b.Y) <= reach) return true;
        }
        return false;
    }

    /// <summary>필압(0~1)과 기본 굵기로 실제 굵기를 계산. 형광펜은 필압을 무시합니다.</summary>
    public static float WidthFor(InkTool tool, float baseWidth, float pressure)
    {
        if (tool == InkTool.Highlighter) return baseWidth;
        pressure = Math.Clamp(pressure, 0f, 1f);
        // 살짝 눌러도 선이 보이고, 세게 누르면 기본 굵기의 약 1.6배까지.
        return baseWidth * (0.25f + 1.35f * MathF.Pow(pressure, 0.9f));
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1, dy = y2 - y1;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-6f) return Distance(px, py, ax, ay);
        float t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0f, 1f);
        return Distance(px, py, ax + t * dx, ay + t * dy);
    }
}
