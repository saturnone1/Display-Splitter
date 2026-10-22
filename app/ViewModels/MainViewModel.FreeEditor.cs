using DesktopSplitter.Models;

namespace DesktopSplitter.ViewModels;

public sealed partial class MainViewModel
{
    private IReadOnlyList<LayoutCut> _liveCustomCuts = Array.Empty<LayoutCut>();
    private bool? _cutVertical;
    private bool _changingFreeDraft;
    public bool IsCustomLayout => SelectedLayout?.IsCustom == true;
    public string EditorHint => !IsCustomLayout
        ? "경계선을 끌거나 아래에 픽셀 크기를 입력하세요. 적용 전에는 실제 화면이 바뀌지 않습니다."
        : _cutVertical is { } vertical ? $"미리 보기에서 자를 위치를 클릭하세요 · {(vertical ? "세로" : "가로")} 경계선"
        : Segments.Count >= 9 ? "9칸을 편집 중입니다. 경계선을 끌어 위치를 조절하거나 마지막 자르기를 취소하세요."
        : "가로·세로 자르기를 고른 뒤 원하는 칸의 자를 위치를 클릭하세요. 경계선은 끌어서 조절할 수 있습니다.";
    public RelayCommand CutVerticalCommand { get; private set; } = null!;
    public RelayCommand CutHorizontalCommand { get; private set; } = null!;
    public RelayCommand UndoCutCommand { get; private set; } = null!;

    private void InitializeFreeEditor()
    {
        bool CanCut() => IsCustomLayout && _geometryValid && !IsBusy && !_closing && Segments.Count < 9;
        CutVerticalCommand = new RelayCommand(() => ArmCut(true), CanCut);
        CutHorizontalCommand = new RelayCommand(() => ArmCut(false), CanCut);
        UndoCutCommand = new RelayCommand(() => {
            _cutVertical = null;
            SetCustomLayout(SelectedLayout!.CustomCuts.SkipLast(1).ToArray(), requirePixelGeometry: false);
        }, () => IsCustomLayout && !IsBusy && !_closing && SelectedLayout!.CustomCuts.Count > 0);
    }

    private void ArmCut(bool vertical)
    {
        _cutVertical = vertical;
        OnPropertyChanged(nameof(EditorHint));
    }

    private void RaiseFreeEditorCommands()
    {
        CutVerticalCommand?.RaiseCanExecuteChanged();
        CutHorizontalCommand?.RaiseCanExecuteChanged();
        UndoCutCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(EditorHint));
    }

    private void SetCustomLayout(IReadOnlyList<LayoutCut> cuts, bool requirePixelGeometry = true)
    {
        var layout = LayoutDefinition.CreateCustom(cuts);
        // Validate pixel geometry before changing the draft. A child must retain at least one pixel.
        if (requirePixelGeometry) layout.ComputePhysRects(SelectedMonitor?.CurrentWidth ?? 1920, SelectedMonitor?.CurrentHeight ?? 1080);
        int index = Layouts.ToList().FindIndex(item => item.IsCustom);
        _suppressSegmentRebuild = true;
        try { Layouts[index] = layout; }
        finally { _suppressSegmentRebuild = false; }
        _changingFreeDraft = true;
        try { SelectedLayout = layout; }
        finally { _changingFreeDraft = false; }
    }

    public void CutAt(int slot, double canvasX, double canvasY)
    {
        if (!IsCustomLayout || !_geometryValid || IsBusy || _closing || _cutVertical is not { } vertical
            || SelectedLayout!.CustomCuts.Count >= FreeLayout.MaxCuts || SelectedMonitor is null) return;
        var geometry = FreeLayout.Compute(SelectedLayout.CustomCuts, SelectedMonitor.CurrentWidth, SelectedMonitor.CurrentHeight);
        if (slot < 0 || slot >= geometry.Rects.Count || !double.IsFinite(canvasX) || !double.IsFinite(canvasY)) return;
        var r = geometry.Rects[slot];
        double x = canvasX * SelectedMonitor.CurrentWidth / CanvasWidth;
        double y = canvasY * SelectedMonitor.CurrentHeight / CanvasHeight;
        if (x < r.X || x > r.Right || y < r.Y || y > r.Bottom) return;
        int extent = vertical ? r.W : r.H;
        if (extent < 2) return;
        int first = Math.Clamp((int)Math.Round(vertical ? x - r.X : y - r.Y), 1, extent - 1);
        _cutVertical = null;
        SetCustomLayout(SelectedLayout.CustomCuts.Append(new LayoutCut(slot, vertical, (double)first / extent)).ToArray());
    }

    private void RebuildFreeSplitters()
    {
        var layout = SelectedLayout!;
        var monitor = SelectedMonitor!;
        var geometry = FreeLayout.Compute(layout.CustomCuts, monitor.CurrentWidth, monitor.CurrentHeight);
        if (Splitters.Count != layout.CustomCuts.Count
            || !Splitters.Select(s => s.IsVertical).SequenceEqual(layout.CustomCuts.Select(c => c.Vertical))) {
            Splitters.Clear();
            for (int i = 0; i < layout.CustomCuts.Count; i++) Splitters.Add(new SplitterVisual(i, layout.CustomCuts[i].Vertical, i));
        }
        for (int i = 0; i < layout.CustomCuts.Count; i++) {
            var cut = layout.CustomCuts[i];
            var p = geometry.CutParents[i];
            var splitter = Splitters[i];
            double sx = CanvasWidth / monitor.CurrentWidth, sy = CanvasHeight / monitor.CurrentHeight;
            int first = Math.Clamp((int)Math.Round((cut.Vertical ? p.W : p.H) * cut.Ratio), 1, (cut.Vertical ? p.W : p.H) - 1);
            splitter.X = (p.X + (cut.Vertical ? first : 0)) * sx - (cut.Vertical ? SplitterVisual.Thickness / 2 : 0);
            splitter.Y = (p.Y + (cut.Vertical ? 0 : first)) * sy - (cut.Vertical ? 0 : SplitterVisual.Thickness / 2);
            splitter.Length = cut.Vertical ? p.H * sy : p.W * sx;
        }
    }

    private void DragFreeSplitter(SplitterVisual splitter, double canvasX, double canvasY)
    {
        var monitor = SelectedMonitor!;
        var cuts = SelectedLayout!.CustomCuts.ToArray();
        if (splitter.Id < 0 || splitter.Id >= cuts.Length) return;
        var parent = FreeLayout.Compute(cuts, monitor.CurrentWidth, monitor.CurrentHeight).CutParents[splitter.Id];
        bool vertical = cuts[splitter.Id].Vertical;
        double target = (vertical ? canvasX : canvasY) - _dragGrabOffset;
        target *= vertical ? monitor.CurrentWidth / CanvasWidth : monitor.CurrentHeight / CanvasHeight;
        if (!double.IsFinite(target)) return;
        int extent = vertical ? parent.W : parent.H;
        int first = Math.Clamp((int)Math.Round(target - (vertical ? parent.X : parent.Y)), 1, extent - 1);
        cuts[splitter.Id] = cuts[splitter.Id] with { Ratio = (double)first / extent };
        try { SetCustomLayout(cuts); }
        catch (ArgumentException) { /* A nested child cannot be reduced to zero pixels. Keep the last valid draft. */ }
    }
}
