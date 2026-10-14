using InkNote.Core.Ink;

namespace InkNote.App;

public enum ToolKind
{
    Pen,
    Highlighter,
    Eraser,
}

/// <summary>툴바에서 고른 현재 도구. 펜과 형광펜은 색·굵기를 따로 기억합니다.</summary>
public sealed class ToolState
{
    // 굵기 단계(1~5)별 기본 굵기, 포인트 단위. 펜은 여기에 필압이 곱해집니다.
    public static readonly float[] PenWidths = { 0.8f, 1.2f, 1.8f, 2.6f, 3.6f };
    public static readonly float[] HighlighterWidths = { 8f, 12f, 16f, 22f, 28f };

    public ToolKind Kind { get; set; } = ToolKind.Pen;
    public InkColor PenColor { get; set; } = InkColor.Black;
    public InkColor HighlighterColor { get; set; } = InkColor.Yellow;
    public int PenLevel { get; set; } = 2;
    public int HighlighterLevel { get; set; } = 3;

    /// <summary>지우개 반지름(화면 DIP). 확대 배율과 상관없이 손끝 느낌이 같도록 화면 기준입니다.</summary>
    public const float EraserRadiusDip = 10f;

    public InkTool InkTool => Kind == ToolKind.Highlighter ? InkTool.Highlighter : InkTool.Pen;
    public InkColor CurrentColor => Kind == ToolKind.Highlighter ? HighlighterColor : PenColor;
    public float CurrentBaseWidth => Kind == ToolKind.Highlighter
        ? HighlighterWidths[HighlighterLevel - 1]
        : PenWidths[PenLevel - 1];
}
