using InkNote.Core.Ink;
using InkNote.Core.Pdfium;
using Xunit;

namespace InkNote.Core.Tests;

public class PdfInkRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inknote-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static InkStroke PenStroke(float x0, float y0, float x1, float y1, int n = 20)
    {
        var s = new InkStroke(InkTool.Pen, InkColor.Blue);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);
            s.Points.Add(new InkPoint(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, InkStroke.WidthFor(InkTool.Pen, 2f, t)));
        }
        return s;
    }

    private string WriteBlank(int pages = 2, int rotate = 0)
    {
        string path = Path.Combine(_dir, $"blank-{rotate}.pdf");
        File.WriteAllBytes(path, PdfFactory.CreateBlank(pages, rotate: rotate));
        return path;
    }

    [Fact]
    public void SavedStrokesComeBackWithPressureAndColor()
    {
        string path = WriteBlank();
        var pen = PenStroke(100, 100, 300, 160);
        var hl = new InkStroke(InkTool.Highlighter, InkColor.Yellow,
            new[] { new InkPoint(80, 400, 12), new InkPoint(400, 410, 12) });

        using (var doc = PdfDocument.Open(path))
        {
            Assert.Equal(2, doc.PageCount);
            Assert.All(doc.ExtractInk(), p => Assert.Empty(p));
            doc.Save(path, new IReadOnlyList<InkStroke>[] { new[] { pen, hl }, Array.Empty<InkStroke>() });
        }

        using (var doc = PdfDocument.Open(path))
        {
            var pages = doc.ExtractInk();
            Assert.Equal(2, pages[0].Count);
            Assert.Empty(pages[1]);

            var back = pages[0][0];
            Assert.Equal(InkTool.Pen, back.Tool);
            Assert.Equal(InkColor.Blue, back.Color);
            Assert.Equal(pen.Points.Count, back.Points.Count);
            for (int i = 0; i < pen.Points.Count; i++)
            {
                Assert.Equal(pen.Points[i].X, back.Points[i].X, 0.05f);
                Assert.Equal(pen.Points[i].Y, back.Points[i].Y, 0.05f);
                Assert.Equal(pen.Points[i].Width, back.Points[i].Width, 0.01f);
            }
            Assert.Equal(InkTool.Highlighter, pages[0][1].Tool);
        }
    }

    [Fact]
    public void SavingTwiceDoesNotDuplicateAnnotations()
    {
        string path = WriteBlank(1);
        var strokes = new IReadOnlyList<InkStroke>[] { new[] { PenStroke(50, 50, 200, 200) } };
        using (var doc = PdfDocument.Open(path))
        {
            doc.ExtractInk();
            doc.Save(path, strokes);
            doc.Save(path, strokes);
        }
        using var reopened = PdfDocument.Open(path);
        Assert.Single(reopened.ExtractInk()[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void InkAppearsWhereItWasDrawnOnRotatedPages(int rotate)
    {
        string path = WriteBlank(1, rotate);
        float w, h;
        using (var doc = PdfDocument.Open(path))
        {
            (w, h) = doc.GetPageSize(0);
            // 화면에서 보이는 왼쪽 위 사분면에만 가로선을 긋습니다.
            doc.Save(path, new IReadOnlyList<InkStroke>[] { new[] { PenStroke(w * 0.1f, h * 0.2f, w * 0.4f, h * 0.2f) } });
        }
        if (rotate is 90 or 270) Assert.True(w > h);

        using var saved = PdfDocument.Open(path); // ExtractInk를 부르지 않으면 주석이 렌더링됩니다.
        int pw = (int)w, ph = (int)h;
        var px = saved.RenderPage(0, pw, ph);
        Assert.True(IsInked(px, pw, (int)(w * 0.25f), (int)(h * 0.2f)), "획 위치에 잉크가 없습니다.");
        Assert.False(IsInked(px, pw, (int)(w * 0.75f), (int)(h * 0.8f)), "엉뚱한 위치에 잉크가 있습니다.");
    }

    [Fact]
    public void HighlighterIsTranslucentInRenderedOutput()
    {
        string path = WriteBlank(1);
        var hl = new InkStroke(InkTool.Highlighter, InkColor.Yellow,
            new[] { new InkPoint(100, 100, 20), new InkPoint(300, 100, 20) });
        using (var doc = PdfDocument.Open(path))
            doc.Save(path, new IReadOnlyList<InkStroke>[] { new[] { hl } });

        using var saved = PdfDocument.Open(path);
        var px = saved.RenderPage(0, 595, 842);
        int i = (100 * 595 + 200) * 4; // BGRA
        // 불투명 노랑(FF E0 3D)이면 파랑 채널이 0x3D, 35%면 흰 바탕과 섞여 훨씬 큽니다.
        Assert.True(px[i] > 0x90, $"형광펜이 불투명합니다 (B={px[i]:X2})");
        Assert.True(px[i] < 0xF0, $"형광펜이 보이지 않습니다 (B={px[i]:X2})");
    }

    private static bool IsInked(byte[] bgra, int width, int cx, int cy)
    {
        for (int y = cy - 3; y <= cy + 3; y++)
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                int i = (y * width + x) * 4;
                if (bgra[i] < 250 || bgra[i + 1] < 250 || bgra[i + 2] < 250) return true;
            }
        return false;
    }
}
