using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InkNote.Core.Ink;
using static InkNote.Core.Pdfium.Native;

namespace InkNote.Core.Pdfium;

public sealed class PdfException(string message) : Exception(message);

/// <summary>
/// PDFium 문서 하나. 파일을 메모리로 읽어 열기 때문에 같은 파일에 덮어써 저장할 수 있습니다.
/// 모든 메서드는 스레드 안전합니다(전역 잠금).
/// </summary>
public sealed unsafe class PdfDocument : IDisposable
{
    private IntPtr _doc;
    private void* _data;
    private readonly PageInfo[] _pages;

    private PdfDocument(IntPtr doc, void* data)
    {
        _doc = doc;
        _data = data;
        int count = FPDF_GetPageCount(doc);
        _pages = new PageInfo[count];
        for (int i = 0; i < count; i++) _pages[i] = ReadPageInfo(i);
    }

    public int PageCount => _pages.Length;

    /// <summary>화면에 보이는 방향(회전 반영)의 페이지 크기, 포인트 단위.</summary>
    public (float Width, float Height) GetPageSize(int index) => (_pages[index].Width, _pages[index].Height);

    public PageTransform GetTransform(int index) => _pages[index].Transform;

    public static PdfDocument Open(string path) => Open(File.ReadAllBytes(path));

    public static PdfDocument Open(byte[] bytes)
    {
        lock (Sync)
        {
            EnsureInitialized();
            void* data = NativeMemory.Alloc((nuint)bytes.Length);
            bytes.AsSpan().CopyTo(new Span<byte>(data, bytes.Length));
            IntPtr doc = FPDF_LoadMemDocument64(data, (nuint)bytes.Length, null);
            if (doc == IntPtr.Zero)
            {
                uint err = (uint)FPDF_GetLastError().Value;
                NativeMemory.Free(data);
                throw new PdfException(err switch
                {
                    2 => "파일을 찾을 수 없거나 열 수 없습니다.",
                    3 => "PDF 형식이 아니거나 손상된 파일입니다.",
                    4 => "암호가 걸린 PDF는 아직 지원하지 않습니다.",
                    _ => $"PDF를 열 수 없습니다 (PDFium 오류 {err}).",
                });
            }
            return new PdfDocument(doc, data);
        }
    }

    /// <summary>
    /// 페이지를 BGRA(알파 255) 픽셀로 렌더링합니다. 다른 앱이 넣은 주석은 함께 그려지고,
    /// 이 앱이 만든 필기는 <see cref="ExtractInk"/>로 이미 문서에서 빠져 있어 그려지지 않습니다.
    /// </summary>
    public byte[] RenderPage(int index, int pixelWidth, int pixelHeight)
    {
        var buffer = new byte[checked(pixelWidth * pixelHeight * 4)];
        lock (Sync)
        {
            ThrowIfDisposed();
            IntPtr page = FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) throw new PdfException($"{index + 1}쪽을 불러올 수 없습니다.");
            try
            {
                fixed (byte* p = buffer)
                {
                    IntPtr bmp = FPDFBitmap_CreateEx(pixelWidth, pixelHeight, FPDFBitmap_BGRA, p, pixelWidth * 4);
                    if (bmp == IntPtr.Zero) throw new PdfException("렌더링용 비트맵을 만들 수 없습니다.");
                    FPDFBitmap_FillRect(bmp, 0, 0, pixelWidth, pixelHeight, new CULong(0xFFFFFFFF));
                    FPDF_RenderPageBitmap(bmp, page, 0, 0, pixelWidth, pixelHeight, 0, FPDF_ANNOT);
                    FPDFBitmap_Destroy(bmp);
                }
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
        return buffer;
    }

    /// <summary>
    /// 이 앱이 저장했던 필기 주석을 읽어 획으로 돌려주고 문서에서는 지웁니다.
    /// 그래서 다시 열어도 필압 그대로 지우개·실행 취소가 됩니다. 다른 앱의 주석은 건드리지 않습니다.
    /// </summary>
    public List<InkStroke>[] ExtractInk()
    {
        var result = new List<InkStroke>[PageCount];
        lock (Sync)
        {
            ThrowIfDisposed();
            for (int i = 0; i < PageCount; i++)
            {
                result[i] = new List<InkStroke>();
                IntPtr page = FPDF_LoadPage(_doc, i);
                if (page == IntPtr.Zero) continue;
                try
                {
                    var t = _pages[i].Transform;
                    for (int a = FPDFPage_GetAnnotCount(page) - 1; a >= 0; a--)
                    {
                        IntPtr annot = FPDFPage_GetAnnot(page, a);
                        if (annot == IntPtr.Zero) continue;
                        InkStroke? stroke = null;
                        bool ours = false;
                        try
                        {
                            if (FPDFAnnot_GetSubtype(annot) == FPDF_ANNOT_INK && FPDFAnnot_HasKey(annot, InkAnnotationCodec.MetadataKey) != 0)
                            {
                                ours = true;
                                stroke = InkAnnotationCodec.Decode(GetString(annot, InkAnnotationCodec.MetadataKey), ReadInkPath(annot), t);
                            }
                        }
                        finally
                        {
                            FPDFPage_CloseAnnot(annot);
                        }
                        // 해석에 실패한 주석은 데이터 손실을 막기 위해 문서에 그대로 둡니다.
                        if (ours && stroke != null)
                        {
                            result[i].Insert(0, stroke);
                            FPDFPage_RemoveAnnot(page, a);
                        }
                    }
                }
                finally
                {
                    FPDF_ClosePage(page);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 획을 PDF 잉크 주석(/Subtype /Ink)으로 넣어 저장합니다. 필압은 외관 스트림(/AP)에 굵기가 변하는 선으로
    /// 그려 넣어서 다른 PDF 뷰어에서도 보이고, 앱이 다시 읽을 수 있도록 원본 점·굵기도 함께 기록합니다.
    /// 저장 후 메모리의 문서는 저장 전 상태(필기 주석 없음)로 되돌립니다.
    /// </summary>
    public void Save(string path, IReadOnlyList<IReadOnlyList<InkStroke>> pages)
    {
        if (pages.Count != PageCount) throw new ArgumentException("페이지 수가 문서와 다릅니다.", nameof(pages));
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        lock (Sync)
        {
            ThrowIfDisposed();
            var added = new List<(int Page, int Count)>();
            try
            {
                for (int i = 0; i < PageCount; i++)
                {
                    if (pages[i].Count == 0) continue;
                    int n = AddInkAnnotations(i, pages[i]);
                    added.Add((i, n));
                }
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                {
                    WriteTo(fs);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
                foreach (var (page, count) in added) RemoveLastAnnotations(page, count);
            }
        }
    }

    private int AddInkAnnotations(int pageIndex, IReadOnlyList<InkStroke> strokes)
    {
        IntPtr page = FPDF_LoadPage(_doc, pageIndex);
        if (page == IntPtr.Zero) throw new PdfException($"{pageIndex + 1}쪽을 불러올 수 없습니다.");
        int added = 0;
        try
        {
            var t = _pages[pageIndex].Transform;
            foreach (var stroke in strokes)
            {
                if (stroke.Points.Count == 0) continue;
                var enc = InkAnnotationCodec.Encode(stroke, t);
                IntPtr annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_INK);
                if (annot == IntPtr.Zero) throw new PdfException("잉크 주석을 만들 수 없습니다.");
                added++;
                try
                {
                    var rect = new FS_RECTF { Left = enc.Rect.Left, Top = enc.Rect.Top, Right = enc.Rect.Right, Bottom = enc.Rect.Bottom };
                    FPDFAnnot_SetRect(annot, &rect);
                    // /C, /CA는 외관 스트림을 넣기 전에 설정해야 합니다(PDFium은 /AP가 있으면 색 변경을 거부).
                    FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, stroke.Color.R, stroke.Color.G, stroke.Color.B,
                        (uint)Math.Round(stroke.Opacity * 255));
                    FPDFAnnot_SetBorder(annot, 0, 0, enc.AverageWidth);
                    FPDFAnnot_SetFlags(annot, FPDF_ANNOT_FLAG_PRINT);
                    fixed (FS_POINTF* pts = enc.PagePoints)
                    {
                        if (FPDFAnnot_AddInkStroke(annot, pts, (nuint)enc.PagePoints.Length) < 0)
                            throw new PdfException("잉크 경로를 기록할 수 없습니다.");
                    }
                    FPDFAnnot_SetStringValue(annot, InkAnnotationCodec.MetadataKey, enc.Metadata);
                    AppendAppearance(annot, stroke, enc);
                }
                finally
                {
                    FPDFPage_CloseAnnot(annot);
                }
            }
        }
        catch
        {
            RemoveLastAnnotationsOn(page, added);
            added = 0;
            throw;
        }
        finally
        {
            FPDF_ClosePage(page);
        }
        return added;
    }

    /// <summary>
    /// 외관 스트림(/AP)을 경로 객체로 그립니다. PDFium이 투명도(/CA)와 블렌드 모드(/BM)를
    /// ExtGState 리소스로 써 주므로 PDFium·Acrobat·브라우저 등 어디서나 같은 모양으로 보입니다.
    /// </summary>
    private static void AppendAppearance(IntPtr annot, InkStroke stroke, InkAnnotationCodec.Encoded enc)
    {
        uint alpha = (uint)Math.Round(stroke.Opacity * 255);
        foreach (var run in enc.Runs)
        {
            var pts = enc.PagePoints;
            IntPtr path = FPDFPageObj_CreateNewPath(pts[run.Start].X, pts[run.Start].Y);
            if (path == IntPtr.Zero) throw new PdfException("외관 경로를 만들 수 없습니다.");
            if (run.Start == run.End) FPDFPath_LineTo(path, pts[run.Start].X, pts[run.Start].Y); // 점 하나 = 둥근 점
            for (int i = run.Start + 1; i <= run.End; i++) FPDFPath_LineTo(path, pts[i].X, pts[i].Y);
            FPDFPath_SetDrawMode(path, 0, 1);
            FPDFPageObj_SetStrokeColor(path, stroke.Color.R, stroke.Color.G, stroke.Color.B, alpha);
            FPDFPageObj_SetStrokeWidth(path, run.Width);
            FPDFPageObj_SetLineCap(path, FPDF_LINECAP_ROUND);
            FPDFPageObj_SetLineJoin(path, FPDF_LINEJOIN_ROUND);
            // 형광펜: 곱하기 블렌드라 아래 글자가 흐려지지 않습니다.
            if (stroke.Tool == InkTool.Highlighter) FPDFPageObj_SetBlendMode(path, "Multiply");
            if (FPDFAnnot_AppendObject(annot, path) == 0)
            {
                FPDFPageObj_Destroy(path);
                throw new PdfException("주석 모양(외관 스트림)을 기록할 수 없습니다.");
            }
        }
    }

    private void RemoveLastAnnotations(int pageIndex, int count)
    {
        IntPtr page = FPDF_LoadPage(_doc, pageIndex);
        if (page == IntPtr.Zero) return;
        try { RemoveLastAnnotationsOn(page, count); }
        finally { FPDF_ClosePage(page); }
    }

    private static void RemoveLastAnnotationsOn(IntPtr page, int count)
    {
        for (int k = 0; k < count; k++)
        {
            int last = FPDFPage_GetAnnotCount(page) - 1;
            if (last < 0) break;
            FPDFPage_RemoveAnnot(page, last);
        }
    }

    private void WriteTo(Stream stream)
    {
        var handle = GCHandle.Alloc(stream);
        try
        {
            var fw = new FileWrite
            {
                Version = 1,
                WriteBlock = &WriteBlockCallback,
                StreamHandle = GCHandle.ToIntPtr(handle),
            };
            if (FPDF_SaveAsCopy(_doc, &fw, new CULong(FPDF_NO_INCREMENTAL)) == 0)
                throw new PdfException("PDF를 저장하지 못했습니다.");
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WriteBlockCallback(FileWrite* self, byte* data, CULong size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(self->StreamHandle).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetString(IntPtr annot, string key)
    {
        ulong bytes = FPDFAnnot_GetStringValue(annot, key, null, new CULong(0)).Value;
        if (bytes <= 2) return string.Empty;
        var buf = new char[bytes / 2];
        fixed (char* p = buf)
        {
            FPDFAnnot_GetStringValue(annot, key, p, new CULong((nuint)bytes));
        }
        return new string(buf, 0, buf.Length - 1);
    }

    private static FS_POINTF[] ReadInkPath(IntPtr annot)
    {
        if (FPDFAnnot_GetInkListCount(annot).Value == 0) return Array.Empty<FS_POINTF>();
        ulong n = FPDFAnnot_GetInkListPath(annot, new CULong(0), null, new CULong(0)).Value;
        var pts = new FS_POINTF[n];
        fixed (FS_POINTF* p = pts)
        {
            FPDFAnnot_GetInkListPath(annot, new CULong(0), p, new CULong((nuint)n));
        }
        return pts;
    }

    private PageInfo ReadPageInfo(int index)
    {
        FS_SIZEF size;
        FPDF_GetPageSizeByIndexF(_doc, index, &size);
        IntPtr page = FPDF_LoadPage(_doc, index);
        if (page == IntPtr.Zero) return new PageInfo(size.Width, size.Height, new PageTransform(0, size.Height, 1, 0, 0, -1));
        try
        {
            // 64배 확대한 가상 장치 좌표의 세 모서리를 페이지 좌표로 바꿔 아핀 변환을 구합니다.
            const int S = 64;
            int sx = Math.Max(1, (int)Math.Round(size.Width * S));
            int sy = Math.Max(1, (int)Math.Round(size.Height * S));
            double x0, y0, x1, y1, x2, y2;
            FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, 0, 0, &x0, &y0);
            FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, sx, 0, &x1, &y1);
            FPDF_DeviceToPage(page, 0, 0, sx, sy, 0, 0, sy, &x2, &y2);
            double w = sx / (double)S, h = sy / (double)S;
            var t = new PageTransform(x0, y0, (x1 - x0) / w, (y1 - y0) / w, (x2 - x0) / h, (y2 - y0) / h);
            return new PageInfo(size.Width, size.Height, t);
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_doc == IntPtr.Zero) throw new ObjectDisposedException(nameof(PdfDocument));
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_doc != IntPtr.Zero)
            {
                FPDF_CloseDocument(_doc);
                _doc = IntPtr.Zero;
            }
            if (_data != null)
            {
                NativeMemory.Free(_data);
                _data = null;
            }
        }
    }

    private readonly record struct PageInfo(float Width, float Height, PageTransform Transform);
}

/// <summary>테스트와 샘플용: 빈 페이지로 된 새 PDF를 만듭니다.</summary>
public static unsafe class PdfFactory
{
    public static byte[] CreateBlank(int pages, float width = 595, float height = 842, int rotate = 0)
    {
        lock (Sync)
        {
            EnsureInitialized();
            IntPtr doc = FPDF_CreateNewDocument();
            try
            {
                for (int i = 0; i < pages; i++)
                {
                    IntPtr page = FPDFPage_New(doc, i, width, height);
                    if (rotate != 0) FPDFPage_SetRotation(page, rotate / 90);
                    FPDF_ClosePage(page);
                }
                using var ms = new MemoryStream();
                var handle = GCHandle.Alloc(ms);
                try
                {
                    var fw = new FileWrite { Version = 1, WriteBlock = &Write, StreamHandle = GCHandle.ToIntPtr(handle) };
                    FPDF_SaveAsCopy(doc, &fw, new CULong(FPDF_NO_INCREMENTAL));
                }
                finally
                {
                    handle.Free();
                }
                return ms.ToArray();
            }
            finally
            {
                FPDF_CloseDocument(doc);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Write(FileWrite* self, byte* data, CULong size)
    {
        var stream = (Stream)GCHandle.FromIntPtr(self->StreamHandle).Target!;
        stream.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
        return 1;
    }
}
