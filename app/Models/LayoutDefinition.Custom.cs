namespace DesktopSplitter.Models;

public sealed partial class LayoutDefinition
{
    public IReadOnlyList<LayoutCut> CustomCuts { get; private init; } = Array.Empty<LayoutCut>();
    public bool IsCustom => Kind == LayoutKind.Custom;

    public static bool HasSegmentCount(LayoutKind kind, int count)
        => Enum.IsDefined(kind) && (kind == LayoutKind.Custom ? count is >= 1 and <= 9 : FromKind(kind).SegmentCount == count);

    public static LayoutDefinition CreateCustom(IReadOnlyList<LayoutCut> cuts)
    {
        FreeLayout.Validate(cuts);
        var rects = new List<ZoneRect> { new(0, 0, 1, 1) };
        foreach (var cut in cuts) {
            var p = rects[cut.Slot];
            double first = (cut.Vertical ? p.W : p.H) * cut.Ratio;
            rects[cut.Slot] = cut.Vertical ? p with { W = first } : p with { H = first };
            rects.Insert(cut.Slot + 1, cut.Vertical ? new(p.X + first, p.Y, p.W - first, p.H)
                : new(p.X, p.Y + first, p.W, p.H - first));
        }
        return new(LayoutKind.Custom, "자유 분할", "원하는 위치에서 가로·세로로 자르기", rects.Count, 1,
            Array.Empty<int>(), Array.Empty<int>(),
            rects.Select((r, i) => new LayoutCell(r.X, r.Y, r.W, r.H, i, 0, 1, 1)).ToArray())
            { CustomCuts = Array.AsReadOnly(cuts.ToArray()) };
    }
}
