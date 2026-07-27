using System.Globalization;
using System.Net;
using System.Text;

namespace DriveVisualizer.Core.Snapshots;

/// <summary>
/// Renders daily history snapshots as a self-contained HTML page: stacked bars
/// (one per day, split by category) plus a per-day table. Same light/dark CSS
/// treatment as the scan report; SVG only, no external assets.
/// </summary>
public static class HistoryChart
{
    private static readonly string[] CategoryHexLight =
        ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948", "#6b6b68"];
    private static readonly string[] CategoryHexDark =
        ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767", "#6b6b68"];

    public static string BuildHtml(IReadOnlyList<ScanSnapshot> history)
    {
        var ordered = history.OrderBy(s => s.TimestampUtc).ToList();
        string target = ordered.Count > 0 ? ordered[^1].Target : "";
        var sb = new StringBuilder(32 * 1024);

        var catVarsLight = string.Join(" ", CategoryHexLight.Select((h, i) => $"--cat{i}: {h};"));
        var catVarsDark = string.Join(" ", CategoryHexDark.Select((h, i) => $"--cat{i}: {h};"));

        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Size history — {{WebUtility.HtmlEncode(target)}}</title>
            <style>
              :root {
                color-scheme: light dark;
                --page: #f9f9f7; --surface: #fcfcfb; --ink: #0b0b0b; --ink2: #52514e;
                --grid: #e1e0d9; --border: rgba(11,11,11,.10);
                {{catVarsLight}}
              }
              @media (prefers-color-scheme: dark) {
                :root {
                  --page: #0d0d0d; --surface: #1a1a19; --ink: #ffffff; --ink2: #c3c2b7;
                  --grid: #2c2c2a; --border: rgba(255,255,255,.10);
                  {{catVarsDark}}
                }
              }
              body { font: 14px/1.5 system-ui, "Segoe UI", sans-serif; color: var(--ink); background: var(--page); margin: 0; }
              .page { max-width: 980px; margin: 0 auto; padding: 32px 24px 64px; }
              h1 { font-size: 22px; margin: 0 0 4px; }
              .muted { color: var(--ink2); }
              .card { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-top: 16px; overflow-x: auto; }
              .legend { display: flex; gap: 14px; flex-wrap: wrap; margin: 8px 0 4px; font-size: 12px; color: var(--ink2); }
              .legend i { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 5px; }
              table { border-collapse: collapse; width: 100%; background: var(--surface); border: 1px solid var(--border); }
              th, td { text-align: right; padding: 6px 10px; border-top: 1px solid var(--grid); font-variant-numeric: tabular-nums; font-size: 13px; }
              th:first-child, td:first-child { text-align: left; }
              thead th { border-top: none; font-size: 12px; color: var(--ink2); }
              @media print { body { background: #fff; color: #0b0b0b; } }
            </style></head><body><div class="page">
            """);

        sb.Append($"<h1>Size history</h1><div class=\"muted\">{WebUtility.HtmlEncode(target)} — {ordered.Count} daily snapshots</div>");

        AppendChart(sb, ordered);
        AppendLegend(sb, ordered);
        AppendTable(sb, ordered);

        sb.Append("<p class=\"muted\" style=\"margin-top:24px;font-size:12px\">One snapshot per day is kept while scans run with auto-save on.</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void AppendChart(StringBuilder sb, List<ScanSnapshot> ordered)
    {
        const float plotW = 900f, plotH = 280f, padL = 70f, padB = 30f, padT = 10f;
        float svgW = plotW + padL + 10, svgH = plotH + padB + padT;
        long max = Math.Max(1, ordered.Max(s => s.TotalAllocated));

        sb.Append($"<div class=\"card\"><svg width=\"{svgW}\" height=\"{svgH}\" viewBox=\"0 0 {svgW} {svgH}\" role=\"img\" aria-label=\"Total size per day, stacked by category\">");

        // Horizontal gridlines + byte labels.
        for (int g = 0; g <= 4; g++)
        {
            float y = padT + plotH * g / 4f;
            long value = (long)(max * (4 - g) / 4.0);
            sb.Append($"<line x1=\"{padL}\" y1=\"{F(y)}\" x2=\"{F(padL + plotW)}\" y2=\"{F(y)}\" stroke=\"var(--grid)\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{F(padL - 6)}\" y=\"{F(y + 4)}\" text-anchor=\"end\" font-size=\"11\" fill=\"var(--ink2)\">{ByteFormatter.Format(value)}</text>");
        }

        float step = plotW / ordered.Count;
        float barW = Math.Min(46f, step * 0.7f);
        int labelEvery = Math.Max(1, ordered.Count / 10);

        for (int i = 0; i < ordered.Count; i++)
        {
            var snap = ordered[i];
            float x = padL + step * i + (step - barW) / 2f;
            float yCursor = padT + plotH;
            string day = snap.TimestampUtc.ToLocalTime().ToString("M/d", CultureInfo.InvariantCulture);

            for (int c = 0; c < snap.CategoryBytes.Length && c < CategoryHexDark.Length; c++)
            {
                long bytes = snap.CategoryBytes[c];
                if (bytes <= 0)
                    continue;
                float h = (float)(plotH * bytes / (double)max);
                yCursor -= h;
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(yCursor)}\" width=\"{F(barW)}\" height=\"{F(Math.Max(0.5f, h - 0.5f))}\" fill=\"var(--cat{c})\">" +
                          $"<title>{day}: {WebUtility.HtmlEncode(FileClassification.DisplayNames[c])} — {ByteFormatter.Format(bytes)}</title></rect>");
            }

            if (i % labelEvery == 0 || i == ordered.Count - 1)
                sb.Append($"<text x=\"{F(x + barW / 2)}\" y=\"{F(padT + plotH + 16)}\" text-anchor=\"middle\" font-size=\"11\" fill=\"var(--ink2)\">{day}</text>");
        }

        sb.Append("</svg></div>");
    }

    private static void AppendLegend(StringBuilder sb, List<ScanSnapshot> ordered)
    {
        var used = new bool[FileClassification.CategoryCount];
        foreach (var s in ordered)
            for (int c = 0; c < s.CategoryBytes.Length && c < used.Length; c++)
                used[c] |= s.CategoryBytes[c] > 0;

        sb.Append("<div class=\"legend\">");
        for (int c = 0; c < used.Length; c++)
            if (used[c])
                sb.Append($"<span><i style=\"background:var(--cat{c})\"></i>{WebUtility.HtmlEncode(FileClassification.DisplayNames[c])}</span>");
        sb.Append("</div>");
    }

    private static void AppendTable(StringBuilder sb, List<ScanSnapshot> ordered)
    {
        sb.Append("<h2 style=\"font-size:16px;margin:24px 0 8px\">Per day</h2><table><thead><tr><th>Date</th><th>Total</th><th>Change</th><th>Files</th></tr></thead><tbody>");
        for (int i = 0; i < ordered.Count; i++)
        {
            var snap = ordered[i];
            long delta = i > 0 ? snap.TotalAllocated - ordered[i - 1].TotalAllocated : 0;
            string deltaText = i == 0 ? "—" : delta == 0 ? "0"
                : (delta > 0 ? "+" : "−") + ByteFormatter.Format(Math.Abs(delta));
            sb.Append($"<tr><td>{snap.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}</td>" +
                      $"<td>{ByteFormatter.Format(snap.TotalAllocated)}</td>" +
                      $"<td>{deltaText}</td>" +
                      $"<td>{snap.TotalFiles:N0}</td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
