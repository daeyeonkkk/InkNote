namespace InkNote.Core.Pdfium;

/// <summary>
/// 화면 좌표(포인트, 왼쪽 위 원점, y 아래로)와 PDF 페이지 좌표(사용자 공간, 보통 왼쪽 아래 원점)
/// 사이의 아핀 변환. 페이지 회전(/Rotate)과 크롭 박스 오프셋을 PDFium이 계산한 값으로 반영합니다.
/// </summary>
public readonly struct PageTransform
{
    // page = Origin + x * Ax + y * Ay
    private readonly double _ox, _oy, _axx, _axy, _ayx, _ayy;
    private readonly double _det;

    public PageTransform(double ox, double oy, double axx, double axy, double ayx, double ayy)
    {
        _ox = ox; _oy = oy; _axx = axx; _axy = axy; _ayx = ayx; _ayy = ayy;
        _det = axx * ayy - ayx * axy;
    }

    public (float X, float Y) ViewToPage(float x, float y) =>
        ((float)(_ox + x * _axx + y * _ayx), (float)(_oy + x * _axy + y * _ayy));

    public (float X, float Y) PageToView(float px, float py)
    {
        double dx = px - _ox, dy = py - _oy;
        double x = (dx * _ayy - dy * _ayx) / _det;
        double y = (_axx * dy - _axy * dx) / _det;
        return ((float)x, (float)y);
    }
}
