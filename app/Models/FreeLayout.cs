namespace DesktopSplitter.Models;

/// <summary>Split the zero-based leaf at this step. Ratio is local to that leaf.</summary>
public sealed record LayoutCut(int Slot, bool Vertical, double Ratio);
public sealed record FreeLayoutGeometry(IReadOnlyList<PixelRect> Rects, IReadOnlyList<PixelRect> CutParents);

public static class FreeLayout
{
    public const int MaxCuts = 8; // The driver protocol and live routing support nine outputs.

    public static void Validate(IReadOnlyList<LayoutCut> cuts)
    {
        if (cuts.Count > MaxCuts) throw new ArgumentException("드라이버가 지원하는 분할 화면은 최대 9개입니다.");
        for (int i = 0; i < cuts.Count; i++)
            if (cuts[i] is not { } cut || cut.Slot < 0 || cut.Slot > i
                || !double.IsFinite(cut.Ratio) || cut.Ratio <= 0 || cut.Ratio >= 1)
                throw new ArgumentException("자유 분할의 자르기 위치가 올바르지 않습니다.");
    }

    public static FreeLayoutGeometry Compute(IReadOnlyList<LayoutCut> cuts, int width, int height)
    {
        Validate(cuts);
        if (width < 1 || height < 1) throw new ArgumentException("모니터 크기가 올바르지 않습니다.");
        var rects = new List<PixelRect> { new(0, 0, width, height) };
        var parents = new List<PixelRect>();
        foreach (var cut in cuts) {
            var parent = rects[cut.Slot];
            int extent = cut.Vertical ? parent.W : parent.H;
            if (extent < 2) throw new ArgumentException("이 칸은 더 나누면 0픽셀 영역이 생깁니다.");
            int first = Math.Clamp((int)Math.Round(extent * cut.Ratio), 1, extent - 1);
            parents.Add(parent);
            rects[cut.Slot] = cut.Vertical ? parent with { W = first } : parent with { H = first };
            rects.Insert(cut.Slot + 1, cut.Vertical
                ? new(parent.X + first, parent.Y, parent.W - first, parent.H)
                : new(parent.X, parent.Y + first, parent.W, parent.H - first));
        }
        return new(rects, parents);
    }
}
