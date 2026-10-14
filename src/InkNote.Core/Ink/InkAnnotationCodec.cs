using System.Globalization;
using System.Text;
using InkNote.Core.Pdfium;
using static InkNote.Core.Pdfium.Native;

namespace InkNote.Core.Ink;

/// <summary>
/// 획 ↔ PDF 잉크 주석 변환.
/// - /InkList: 페이지 좌표의 점 (표준, 다른 앱도 읽음)
/// - /AP: 굵기가 변하는 선으로 그린 외관 스트림 (다른 뷰어에서도 필압과 형광펜 투명도가 보임)
/// - /InkNoteData: "1|도구|RRGGBB|굵기,굵기,..." (이 앱이 다시 편집할 수 있게)
/// </summary>
internal static class InkAnnotationCodec
{
    public const string MetadataKey = "InkNoteData";
    private const string Version = "1";

    public sealed record Encoded(FS_POINTF[] PagePoints, InkRect Rect, IReadOnlyList<PathRun> Runs, string Metadata, float AverageWidth);

    /// <summary>PagePoints[Start..End]를 잇는 굵기 Width의 선.</summary>
    public readonly record struct PathRun(float Width, int Start, int End);

    public static Encoded Encode(InkStroke stroke, PageTransform t)
    {
        var pts = new FS_POINTF[stroke.Points.Count];
        float l = float.MaxValue, b = float.MaxValue, r = float.MinValue, top = float.MinValue;
        for (int i = 0; i < pts.Length; i++)
        {
            var p = stroke.Points[i];
            var (x, y) = t.ViewToPage(p.X, p.Y);
            pts[i] = new FS_POINTF { X = x, Y = y };
            float h = p.Width / 2 + 1;
            l = Math.Min(l, x - h); r = Math.Max(r, x + h);
            b = Math.Min(b, y - h); top = Math.Max(top, y + h);
        }
        // FS_RECTF는 PDF 좌표라 Top > Bottom.
        var rect = new InkRect(l, top, r, b);
        float avg = stroke.Points.Average(p => p.Width);
        return new Encoded(pts, rect, BuildRuns(stroke), BuildMetadata(stroke), avg);
    }

    public static InkStroke? Decode(string metadata, FS_POINTF[] pagePoints, PageTransform t)
    {
        var parts = metadata.Split('|');
        if (parts.Length != 4 || parts[0] != Version || pagePoints.Length == 0) return null;
        InkTool tool = parts[1] switch { "pen" => InkTool.Pen, "highlighter" => InkTool.Highlighter, _ => (InkTool)(-1) };
        if (!Enum.IsDefined(tool) || parts[2].Length != 6) return null;
        if (!uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) return null;
        var widths = parts[3].Split(',');
        if (widths.Length != pagePoints.Length) return null;

        var stroke = new InkStroke(tool, new InkColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        for (int i = 0; i < pagePoints.Length; i++)
        {
            if (!float.TryParse(widths[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float w)) return null;
            var (x, y) = t.PageToView(pagePoints[i].X, pagePoints[i].Y);
            stroke.Points.Add(new InkPoint(x, y, w));
        }
        return stroke;
    }

    private static string BuildMetadata(InkStroke s)
    {
        var sb = new StringBuilder();
        sb.Append(Version).Append('|')
          .Append(s.Tool == InkTool.Pen ? "pen" : "highlighter").Append('|')
          .Append($"{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}").Append('|');
        for (int i = 0; i < s.Points.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(F(s.Points[i].Width));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 외관을 그릴 선 묶음. 펜은 굵기가 비슷한 연속 선분끼리 한 경로로 묶고(필압 표현),
    /// 형광펜은 겹친 부분이 진해지지 않도록 전체를 경로 하나로 그립니다.
    /// </summary>
    private static List<PathRun> BuildRuns(InkStroke s)
    {
        var runs = new List<PathRun>();
        var p = s.Points;
        if (p.Count == 1 || s.Tool == InkTool.Highlighter)
        {
            runs.Add(new PathRun(p.Average(x => x.Width), 0, p.Count - 1));
            return runs;
        }
        int start = 0;
        float width = SegmentWidth(p, 1);
        for (int i = 2; i < p.Count; i++)
        {
            float w = SegmentWidth(p, i);
            if (MathF.Abs(w - width) > WidthStep)
            {
                runs.Add(new PathRun(width, start, i - 1));
                start = i - 1;
                width = w;
            }
        }
        runs.Add(new PathRun(width, start, p.Count - 1));
        return runs;
    }

    // 선분 굵기를 이 단위 이상 차이 날 때만 새 경로로 나눕니다(눈으로 구분 안 되는 수준).
    private const float WidthStep = 0.08f;

    private static float SegmentWidth(List<InkPoint> p, int i) => (p[i - 1].Width + p[i].Width) / 2;

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
