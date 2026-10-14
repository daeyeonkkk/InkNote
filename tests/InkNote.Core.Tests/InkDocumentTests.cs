using InkNote.Core.Ink;
using Xunit;

namespace InkNote.Core.Tests;

public class InkDocumentTests
{
    private static InkStroke Line(float y) =>
        new(InkTool.Pen, InkColor.Black, new[] { new InkPoint(10, y, 2), new InkPoint(100, y, 2) });

    [Fact]
    public void EraseUndoRedo()
    {
        var doc = new InkDocument(1);
        var a = Line(10);
        var b = Line(50);
        doc.AddStroke(0, a);
        doc.AddStroke(0, b);

        Assert.Equal(1, doc.EraseAt(0, 55, 50, 3));
        Assert.Equal(new[] { a }, doc.GetStrokes(0));
        Assert.Equal(0, doc.EraseAt(0, 55, 30, 3));

        doc.Undo();
        Assert.Contains(b, doc.GetStrokes(0));
        doc.Undo();
        Assert.Equal(new[] { a }, doc.GetStrokes(0));
        doc.Redo();
        Assert.Equal(2, doc.GetStrokes(0).Count);
        Assert.True(doc.IsDirty);
        doc.MarkSaved();
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void NewStrokeClearsRedo()
    {
        var doc = new InkDocument(1);
        doc.AddStroke(0, Line(10));
        doc.Undo();
        Assert.True(doc.CanRedo);
        doc.AddStroke(0, Line(20));
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void PressureWidensPenButNotHighlighter()
    {
        Assert.True(InkStroke.WidthFor(InkTool.Pen, 2, 1) > InkStroke.WidthFor(InkTool.Pen, 2, 0.2f));
        Assert.Equal(12, InkStroke.WidthFor(InkTool.Highlighter, 12, 0.1f));
    }
}
