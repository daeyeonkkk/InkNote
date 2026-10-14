using InkNote.Core.Ink;
using InkNote.Core.Pdfium;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace InkNote.App;

public sealed partial class MainWindow : Window
{
    private static readonly InkColor[] Palette = { InkColor.Black, InkColor.Blue, InkColor.Red, InkColor.Yellow, InkColor.Green };

    private readonly ToolState _tools = new();
    private readonly List<PageView> _pages = new();
    private PdfDocument? _pdf;
    private InkDocument? _ink;
    private string? _path;
    private bool _penHover;
    private bool _closeConfirmed;
    private bool _busy;
    private int _currentPage;

    public MainWindow(string? initialPath)
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        AppWindow.Closing += OnClosing;
        BuildSwatches();
        SyncToolUi();
        UpdateTitle();

        Root.Loaded += async (_, _) =>
        {
            Root.XamlRoot.Changed += (_, _) => UpdateRenderScales();
            if (initialPath != null) await OpenAsync(initialPath);
        };
    }

    // ───── 파일 ─────

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        InitPicker(picker);
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null) await OpenAsync(file.Path);
    }

    private async Task OpenAsync(string path)
    {
        if (_busy || !await ConfirmDiscardAsync()) return;
        _busy = true;
        try
        {
            var (pdf, ink) = await Task.Run(() =>
            {
                var doc = PdfDocument.Open(path);
                try
                {
                    return (doc, new InkDocument(doc.ExtractInk()));
                }
                catch
                {
                    doc.Dispose();
                    throw;
                }
            });
            CloseDocument();
            _pdf = pdf;
            _ink = ink;
            _path = path;
            _ink.PageChanged += OnInkPageChanged;

            for (int i = 0; i < pdf.PageCount; i++)
            {
                var page = new PageView(i, pdf, ink, _tools, () => Scroller.ZoomFactor);
                page.PenHoverChanged += SetPenHover;
                _pages.Add(page);
                PagesPanel.Children.Add(page);
            }
            EmptyHint.Visibility = Visibility.Collapsed;
            FitWidth();
            UpdateCommands();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("PDF를 열 수 없습니다", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void CloseDocument()
    {
        foreach (var p in _pages) p.Unrealize();
        _pages.Clear();
        PagesPanel.Children.Clear();
        if (_ink != null) _ink.PageChanged -= OnInkPageChanged;
        _ink = null;
        _pdf?.Dispose();
        _pdf = null;
        _path = null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(_path);

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(null);

    /// <summary>path가 null이면 저장 위치를 묻습니다. 저장했으면 true.</summary>
    private async Task<bool> SaveAsync(string? path)
    {
        if (_pdf == null || _ink == null || _busy) return false;
        if (path == null)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("PDF 문서", new List<string> { ".pdf" });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_path ?? "필기") + " 필기";
            InitPicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return false;
            path = file.Path;
        }

        _busy = true;
        StatusText.Text = "저장 중…";
        try
        {
            // 저장하는 동안에도 계속 쓸 수 있도록 지금 획 목록을 복사해서 넘깁니다(획 자체는 바뀌지 않음).
            var snapshot = _ink.AllPages.Select(p => (IReadOnlyList<InkStroke>)p.ToArray()).ToArray();
            var pdf = _pdf;
            await Task.Run(() => pdf.Save(path, snapshot));
            _path = path;
            _ink.MarkSaved();
            UpdateCommands();
            StatusText.Text = $"저장함 · {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            _busy = false;
            UpdateStatus();
            await ShowErrorAsync("저장하지 못했습니다", ex.Message + "\n\n다른 프로그램에서 파일을 열고 있다면 닫고 다시 시도해 주세요.");
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>저장 안 한 필기가 있으면 물어봅니다. 계속해도 되면 true.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (_ink is not { IsDirty: true }) return true;
        var dialog = new ContentDialog
        {
            Title = "저장하지 않은 필기가 있습니다",
            Content = "지금 저장할까요?",
            PrimaryButtonText = "저장",
            SecondaryButtonText = "저장 안 함",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveAsync(_path),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || _ink is not { IsDirty: true }) return;
        args.Cancel = true;
        if (await ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var pdf = items.OfType<StorageFile>().FirstOrDefault(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf != null) await OpenAsync(pdf.Path);
    }

    private void InitPicker(object picker) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

    private async Task ShowErrorAsync(string title, string message)
    {
        await new ContentDialog { Title = title, Content = message, CloseButtonText = "확인", XamlRoot = Root.XamlRoot }.ShowAsync();
    }

    // ───── 도구 ─────

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        _tools.Kind = Enum.Parse<ToolKind>((string)((FrameworkElement)sender).Tag);
        SyncToolUi();
    }

    private void SyncToolUi()
    {
        PenButton.IsChecked = _tools.Kind == ToolKind.Pen;
        HighlighterButton.IsChecked = _tools.Kind == ToolKind.Highlighter;
        EraserButton.IsChecked = _tools.Kind == ToolKind.Eraser;

        bool inking = _tools.Kind != ToolKind.Eraser;
        Swatches.Opacity = WidthSlider.Opacity = inking ? 1 : 0.4;
        Swatches.IsHitTestVisible = WidthSlider.IsEnabled = inking;
        WidthSlider.Value = _tools.Kind == ToolKind.Highlighter ? _tools.HighlighterLevel : _tools.PenLevel;
        foreach (var b in Swatches.Children.OfType<Button>())
        {
            bool selected = inking && (InkColor)b.Tag == _tools.CurrentColor;
            b.BorderBrush = new SolidColorBrush(selected ? Colors.DodgerBlue : Windows.UI.Color.FromArgb(0x60, 0x80, 0x80, 0x80));
            b.BorderThickness = new Thickness(selected ? 3 : 1);
        }
    }

    private void BuildSwatches()
    {
        foreach (var c in Palette)
        {
            var b = new Button
            {
                Tag = c,
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B)),
            };
            b.Resources["ButtonBackgroundPointerOver"] = b.Background;
            b.Resources["ButtonBackgroundPressed"] = b.Background;
            b.Click += (_, _) =>
            {
                if (_tools.Kind == ToolKind.Highlighter) _tools.HighlighterColor = c;
                else _tools.PenColor = c;
                SyncToolUi();
            };
            Swatches.Children.Add(b);
        }
    }

    private void WidthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        int level = Math.Clamp((int)Math.Round(e.NewValue), 1, 5);
        if (_tools.Kind == ToolKind.Highlighter) _tools.HighlighterLevel = level;
        else if (_tools.Kind == ToolKind.Pen) _tools.PenLevel = level;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => _ink?.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => _ink?.Redo();

    private void OnInkPageChanged(int page)
    {
        _pages[page].InvalidateInk();
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        SaveButton.IsEnabled = SaveAsButton.IsEnabled = _pdf != null;
        UndoButton.IsEnabled = _ink?.CanUndo == true;
        RedoButton.IsEnabled = _ink?.CanRedo == true;
        UpdateTitle();
        UpdateStatus();
    }

    private void UpdateTitle()
    {
        string name = _path != null ? Path.GetFileName(_path) : "";
        string dirty = _ink?.IsDirty == true ? "● " : "";
        Title = name.Length > 0 ? $"{dirty}{name} - InkNote" : "InkNote";
    }

    // ───── 스크롤 · 확대 ─────

    /// <summary>펜이 화면 위에 있는 동안은 터치 스크롤·확대를 막습니다(손바닥 무시).</summary>
    private void SetPenHover(bool hovering)
    {
        if (_penHover == hovering) return;
        _penHover = hovering;
        Scroller.VerticalScrollMode = hovering ? ScrollMode.Disabled : ScrollMode.Enabled;
        Scroller.HorizontalScrollMode = hovering ? ScrollMode.Disabled : ScrollMode.Auto;
        Scroller.ZoomMode = hovering ? ZoomMode.Disabled : ZoomMode.Enabled;
    }

    private void Scroller_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) SetPenHover(false);
    }

    private void FitWidth()
    {
        Scroller.UpdateLayout();
        if (_pages.Count == 0 || Scroller.ViewportWidth <= 0) return;
        double widest = _pages.Max(p => p.Width) + PagesPanel.Padding.Left + PagesPanel.Padding.Right;
        float zoom = (float)Math.Clamp(Scroller.ViewportWidth / widest, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        Scroller.ChangeView(0, 0, zoom, disableAnimation: true);
        UpdateRealization();
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateRealization();
        if (!e.IsIntermediate) UpdateRenderScales();
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRealization();

    private void UpdateRenderScales()
    {
        foreach (var p in _pages)
            if (p.IsRealized) p.UpdateRenderScale();
    }

    /// <summary>화면 근처(위아래 한 화면 여유) 페이지만 캔버스를 붙이고 나머지는 뗍니다.</summary>
    private void UpdateRealization()
    {
        if (_pages.Count == 0) return;
        double vh = Scroller.ViewportHeight;
        var near = new Rect(0, -vh, Math.Max(1, Scroller.ViewportWidth), vh * 3);
        double middle = vh / 2;
        int current = 0;
        foreach (var p in _pages)
        {
            var b = p.TransformToVisual(Scroller).TransformBounds(new Rect(0, 0, p.Width, p.Height));
            bool visible = b.Bottom >= near.Top && b.Top <= near.Bottom;
            if (visible) p.Realize();
            else p.Unrealize();
            if (b.Top <= middle) current = p.PageIndex;
        }
        _currentPage = current;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_busy) return;
        StatusText.Text = _pdf == null ? "" : $"{_currentPage + 1} / {_pdf.PageCount}쪽 · {Scroller.ZoomFactor * 100:0}%";
    }
}
