namespace PinCC.UI;

public enum Corner { TopRight, TopLeft, BottomRight, BottomLeft }

/// <summary>Where a tiled card goes: a rect, shown as a pill because space ran out, or not shown at all ("+N").</summary>
public readonly record struct Slot(Px Rect, bool Pill, bool Hidden);

/// <summary>Pure placement math in physical px, one monitor at a time. No WPF, so it is unit-tested.</summary>
public static class Layout
{
    /// <summary>
    /// Masonry columns from <paramref name="corner"/>: cards flow along a column (as wide as its widest card, aligned to
    /// the outer edge), then the next column starts one gap further in. Spots that touch an obstacle (free cards, the hub)
    /// are skipped, so tiled cards never overlap anything. Never cascades: when space runs out, cards turn into pills
    /// from the end of the order; if even pills do not fit, the rest are hidden (the hub shows "+N").
    /// </summary>
    public static List<Slot> Tile(Px work, IReadOnlyList<(int W, int H, int PillH)> items, IReadOnlyList<Px> obstacles, int gap, int inset, Corner corner)
    {
        for (int expanded = items.Count; ; expanded--)
        {
            var sizes = items.Select((it, i) => (it.W, i < expanded ? it.H : Math.Min(it.H, it.PillH))).ToList();
            var placed = Place(work, sizes, obstacles, gap, inset, corner);
            if (expanded == 0 || placed.All(p => p != null))
                return placed.Select((p, i) => new Slot(p ?? default, i >= expanded && items[i].H > items[i].PillH, p == null)).ToList();
        }
    }

    static List<Px?> Place(Px work, IReadOnlyList<(int W, int H)> sizes, IReadOnlyList<Px> obstacles, int gap, int inset, Corner corner)
    {
        bool flipX = corner is Corner.TopRight or Corner.BottomRight;
        bool flipY = corner is Corner.BottomLeft or Corner.BottomRight;
        Px Mirror(Px r) => new(flipX ? work.X + work.Right - r.Right : r.X, flipY ? work.Y + work.Bottom - r.Bottom : r.Y, r.W, r.H);

        var obs = obstacles.Select(o => Mirror(o).Inflate(gap)).ToList();
        var result = new List<Px?>(sizes.Count);
        int top = work.Y + inset, bottom = work.Bottom - inset, right = work.Right - inset;
        int x = work.X + inset, y = top, colW = 0;
        bool full = false;

        foreach (var (w, h) in sizes)
        {
            Px? placed = null;
            while (!full)
            {
                if (y > top && y + h > bottom) { x += (colW > 0 ? colW : w) + gap; y = top; colW = 0; }
                if (x + w > right && x > work.X + inset) { full = true; break; }
                var cand = new Px(x, y, w, h);
                var hit = obs.Where(o => o.Intersects(cand)).OrderBy(o => o.Bottom).FirstOrDefault();
                if (hit.W > 0)
                {
                    y = Math.Max(y + 1, hit.Bottom);
                    if (y + h > bottom) { x += Math.Max(colW, w) + gap; y = top; colW = 0; }
                    continue;
                }
                placed = cand;
                y += h + gap;
                colW = Math.Max(colW, w);
                break;
            }
            result.Add(placed is { } p ? Mirror(p) : null);
        }
        return result;
    }

    /// <summary>
    /// A dropped (or grown) free card that overlaps obstacles is pushed out along the axis of least penetration,
    /// keeping one gap of air, and kept inside the work area.
    /// </summary>
    public static Px PushOut(Px r, IReadOnlyList<Px> obstacles, Px work, int gap)
    {
        r = r.ClampInto(work);
        for (int i = 0; i < 12; i++)
        {
            var hit = obstacles.Select(o => o.Inflate(gap)).FirstOrDefault(o => o.Intersects(r));
            if (hit.W == 0) break;
            int left = r.Right - hit.X, right = hit.Right - r.X, up = r.Bottom - hit.Y, down = hit.Bottom - r.Y;
            // Prefer directions that stay inside the work area.
            var moves = new[] { (left, -left, 0), (right, right, 0), (up, 0, -up), (down, 0, down) }
                .OrderBy(m => work.Contains(r.X + m.Item2, r.Y + m.Item3) && work.Contains(r.Right - 1 + m.Item2, r.Bottom - 1 + m.Item3) ? 0 : 1)
                .ThenBy(m => m.Item1).First();
            r = r.Offset(moves.Item2, moves.Item3).ClampInto(work);
        }
        return r;
    }
}
