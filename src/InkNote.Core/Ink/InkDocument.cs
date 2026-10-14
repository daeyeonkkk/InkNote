namespace InkNote.Core.Ink;

/// <summary>
/// 페이지별 획 목록과 실행 취소/다시 실행 기록. UI와 무관하게 동작합니다.
/// </summary>
public sealed class InkDocument
{
    private readonly List<InkStroke>[] _pages;
    private readonly Stack<Change> _undo = new();
    private readonly Stack<Change> _redo = new();

    public InkDocument(int pageCount)
    {
        _pages = new List<InkStroke>[pageCount];
        for (int i = 0; i < pageCount; i++) _pages[i] = new List<InkStroke>();
    }

    public InkDocument(IReadOnlyList<List<InkStroke>> pages)
    {
        _pages = pages.Select(p => new List<InkStroke>(p)).ToArray();
    }

    /// <summary>획이 바뀐 페이지 번호로 발생. 화면 다시 그리기에 씁니다.</summary>
    public event Action<int>? PageChanged;

    public int PageCount => _pages.Length;
    public bool IsDirty { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public IReadOnlyList<InkStroke> GetStrokes(int page) => _pages[page];

    public IReadOnlyList<IReadOnlyList<InkStroke>> AllPages => _pages;

    public void AddStroke(int page, InkStroke stroke)
    {
        if (stroke.Points.Count == 0) return;
        Apply(new Change(page, Added: new[] { stroke }, Removed: Array.Empty<InkStroke>()), record: true);
    }

    /// <summary>(x, y) 근처 획을 지우고 지운 개수를 돌려줍니다.</summary>
    public int EraseAt(int page, float x, float y, float radius)
    {
        var hit = _pages[page].Where(s => s.HitTest(x, y, radius)).ToArray();
        if (hit.Length == 0) return 0;
        Apply(new Change(page, Added: Array.Empty<InkStroke>(), Removed: hit), record: true);
        return hit.Length;
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var c = _undo.Pop();
        Apply(c.Inverse(), record: false);
        _redo.Push(c);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var c = _redo.Pop();
        Apply(c, record: false);
        _undo.Push(c);
    }

    public void MarkSaved() => IsDirty = false;

    private void Apply(Change c, bool record)
    {
        var list = _pages[c.Page];
        foreach (var s in c.Removed) list.Remove(s);
        list.AddRange(c.Added);
        if (record)
        {
            _undo.Push(c);
            _redo.Clear();
        }
        IsDirty = true;
        PageChanged?.Invoke(c.Page);
    }

    private sealed record Change(int Page, IReadOnlyList<InkStroke> Added, IReadOnlyList<InkStroke> Removed)
    {
        public Change Inverse() => new(Page, Removed, Added);
    }
}
