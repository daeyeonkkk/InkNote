using System.Runtime.InteropServices;

namespace InkNote.Core.Pdfium;

/// <summary>
/// PDFium C API P/Invoke 선언. 필요한 함수만 둡니다.
/// C의 unsigned long은 Windows에서 32비트, Linux에서 64비트라 CULong을 씁니다.
/// PDFium은 스레드 안전하지 않으므로 모든 호출은 <see cref="Native.Sync"/> 잠금 안에서 해야 합니다.
/// </summary>
internal static unsafe class Native
{
    private const string Lib = "pdfium";

    public static readonly object Sync = new();

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_NO_NATIVETEXT = 0x04;
    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDFANNOT_COLORTYPE_Color = 0;
    public const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;
    public const uint FPDF_NO_INCREMENTAL = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_POINTF { public float X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF { public float Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_SIZEF { public float Width, Height; }

    /// <summary>FPDF_FILEWRITE 뒤에 관리 스트림 핸들을 덧붙인 구조체. pThis로 이 구조체 주소가 넘어옵니다.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileWrite
    {
        public int Version;
        public delegate* unmanaged[Cdecl]<FileWrite*, byte*, CULong, int> WriteBlock;
        public IntPtr StreamHandle;
    }

    // fpdfview.h
    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern CULong FPDF_GetLastError();
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadMemDocument64(void* data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Lib)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int pageIndex, FS_SIZEF* size);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, double* pageX, double* pageY);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, CULong color);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    // fpdf_edit.h (테스트용 새 문서 생성)
    [DllImport(Lib)] public static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Lib)] public static extern IntPtr FPDFPage_New(IntPtr document, int pageIndex, double width, double height);
    [DllImport(Lib)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);

    // fpdf_edit.h (주석 외관을 경로 객체로 그리기: 투명도·블렌드가 ExtGState로 함께 기록됨)
    public const int FPDF_LINECAP_ROUND = 1;
    public const int FPDF_LINEJOIN_ROUND = 1;
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_LineTo(IntPtr path, float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeWidth(IntPtr obj, float width);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineCap(IntPtr obj, int cap);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineJoin(IntPtr obj, int join);
    [DllImport(Lib)] public static extern void FPDFPageObj_SetBlendMode(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string mode);
    [DllImport(Lib)] public static extern void FPDFPageObj_Destroy(IntPtr obj);

    // fpdf_save.h
    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(IntPtr document, FileWrite* fileWrite, CULong flags);

    // fpdf_annot.h
    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetColor(IntPtr annot, int type, uint* r, uint* g, uint* b, uint* a);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetRect(IntPtr annot, FS_RECTF* rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib)] public static extern int FPDFAnnot_AddInkStroke(IntPtr annot, FS_POINTF* points, nuint pointCount);
    [DllImport(Lib)] public static extern CULong FPDFAnnot_GetInkListCount(IntPtr annot);
    [DllImport(Lib)] public static extern CULong FPDFAnnot_GetInkListPath(IntPtr annot, CULong pathIndex, FS_POINTF* buffer, CULong length);
    [DllImport(Lib)] public static extern int FPDFAnnot_HasKey(IntPtr annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Lib)] public static extern CULong FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, char* buffer, CULong bufferLength);
    [DllImport(Lib)] public static extern int FPDFAnnot_AppendObject(IntPtr annot, IntPtr obj);

    static Native()
    {
        FPDF_InitLibrary();
    }

    /// <summary>정적 생성자(라이브러리 초기화)를 확실히 실행시키기 위한 호출.</summary>
    public static void EnsureInitialized() { }
}
