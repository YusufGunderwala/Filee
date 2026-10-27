using System.Globalization;
using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;

namespace Filee.Engines.Office;

/// <summary>
/// Writes high-fidelity vector PDF documents from the <see cref="HDocument"/> model using SkiaSharp.
/// Supports: paragraphs, tables, images, text boxes, shapes, headers/footers, multi-column,
/// superscript/subscript, highlight, line breaks, tabs, and proper SkiaSharp font metrics.
/// </summary>
internal static class DocumentPdfWriter
{
    // 1 HWPUNIT = 1/7200 inch; 1 inch = 72 pt  =>  1 HWPUNIT = 72/7200 = 0.01 pt
    private const float HwpToPt = 72f / 7200f;
    private const float DefaultFontSizePt = 10.5f;
    private const float DefaultLineSpacingPct = 1.35f;  // 135 % of font size

    // ─────────────────────────────────────────────────────────────────────────────
    // PUBLIC ENTRY POINT
    // ─────────────────────────────────────────────────────────────────────────────

    public static void Write(HDocument document, string outputPath, CancellationToken ct = default)
    {
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title      = document.Title ?? "Document";
        metadata.Creator    = "Filee";
        metadata.Producer   = "Filee (Built-in Vector PDF Engine)";
        metadata.Creation   = DateTime.Now;
        metadata.Modified   = DateTime.Now;
        metadata.RasterDpi  = 300;
        metadata.EncodingQuality = 95;

        using var stream = File.Create(outputPath);
        using var pdf    = SKDocument.CreatePdf(stream, metadata);

        var sections = document.Sections.Count > 0 ? document.Sections : [new HSection()];
        var pageNum  = 1;

        foreach (var section in sections)
        {
            ct.ThrowIfCancellationRequested();
            pageNum = RenderSection(pdf, section, pageNum, ct);
        }

        pdf.Close();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SECTION RENDERER
    // ─────────────────────────────────────────────────────────────────────────────

    private static int RenderSection(SKDocument pdf, HSection section, int startPageNum, CancellationToken ct)
    {
        var pg = section.Page;
        float pgW  = pg != null ? Math.Max(200f, pg.Width  * HwpToPt) : 595.28f;
        float pgH  = pg != null ? Math.Max(200f, pg.Height * HwpToPt) : 841.89f;
        float mL   = Math.Max(36f, pg != null ? pg.Left   * HwpToPt : 72f);
        float mR   = Math.Max(36f, pg != null ? pg.Right  * HwpToPt : 72f);
        float mT   = Math.Max(36f, pg != null ? pg.Top    * HwpToPt : 72f);
        float mB   = Math.Max(36f, pg != null ? pg.Bottom * HwpToPt : 72f);
        float hdrH = pg != null ? pg.Header * HwpToPt : 0f;
        float ftrH = pg != null ? pg.Footer * HwpToPt : 0f;

        // Body top starts below the header area
        float bodyTop = mT + hdrH;
        float bodyBot = pgH - mB - ftrH;
        float bodyW   = pgW - mL - mR;

        // Multi-column
        int colCount = section.Columns.Count > 1 ? section.Columns.Count : 1;
        float colGap = section.Columns.Gap * HwpToPt;
        float colW   = (bodyW - colGap * (colCount - 1)) / colCount;

        // State
        SKCanvas? canvas = null;
        int pageNum = startPageNum;
        int colIdx  = 0;

        float GetColLeft() => mL + colIdx * (colW + colGap);

        float currentY = bodyTop;

        void BeginPage()
        {
            canvas    = pdf.BeginPage(pgW, pgH);
            pageNum++;
            currentY  = bodyTop;
            colIdx    = 0;

            // Draw headers
            DrawHeaderFooterBlocks(canvas, section.Headers, pgW, mL, mT, bodyW, pageNum, section.HideFirstHeader && pageNum == startPageNum + 1);
        }

        void EndPage()
        {
            if (canvas == null) return;
            DrawHeaderFooterBlocks(canvas, section.Footers, pgW, mL, pgH - mB - ftrH, bodyW, pageNum, section.HideFirstFooter && pageNum == startPageNum + 1);
            DrawPageNumber(canvas, pgW, pgH - mB + (ftrH > 4f ? ftrH / 2f : 6f), pageNum);
            pdf.EndPage();
            canvas = null;
        }

        void EnsurePage()
        {
            if (canvas == null) BeginPage();
        }

        void AdvanceColumn()
        {
            colIdx++;
            if (colIdx >= colCount)
            {
                EndPage();
                BeginPage();
            }
            else
            {
                currentY = bodyTop;
            }
        }

        void EnsureRoom(float needed)
        {
            EnsurePage();
            if (currentY + needed > bodyBot)
                AdvanceColumn();
        }

        EnsurePage();

        // Floating images collected per page (drawn last so they overlap text)
        var floatingImages = new List<(SKCanvas C, string Path, SKRect Rect)>();

        foreach (var block in section.Blocks)
        {
            ct.ThrowIfCancellationRequested();

            float colLeft = GetColLeft();

            switch (block)
            {
                case HParagraph para:
                    RenderParagraph(para, canvas!, pdf, ref currentY, ref colIdx, ref pageNum,
                        colLeft, colW, bodyTop, bodyBot,
                        EnsureRoom, AdvanceColumn, EnsurePage,
                        pgW, pgH, section,
                        floatingImages, ct);
                    break;

                case HTable tbl when tbl.Rows.Count > 0:
                    RenderTable(tbl, canvas!, pdf, ref currentY, ref colIdx, ref pageNum,
                        colLeft, colW, bodyTop, bodyBot,
                        EnsureRoom, AdvanceColumn, EnsurePage,
                        pgW, pgH, section, ct);
                    break;
            }
        }

        if (canvas != null) EndPage();

        return pageNum;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // PARAGRAPH RENDERER
    // ─────────────────────────────────────────────────────────────────────────────

    private static void RenderParagraph(
        HParagraph para, SKCanvas canvas, SKDocument pdf,
        ref float y, ref int colIdx, ref int pageNum,
        float x, float w, float bodyTop, float bodyBot,
        Action<float> ensureRoom, Action advanceCol, Action ensurePage,
        float pgW, float pgH, HSection section,
        List<(SKCanvas, string, SKRect)> floatingImages,
        CancellationToken ct)
    {
        if (para.PageBreakBefore && y > bodyTop + 10f)
            advanceCol();

        // Spacing before
        float before = (para.Format.Before ?? 0) * HwpToPt;
        if (before > 0f && y > bodyTop)
            y += Math.Min(36f, before);

        // Heading overrides
        int hlv = para.HeadingLevel;
        float baseSz = DefaultFontSizePt;
        bool baseBold = false;

        if (hlv > 0)
        {
            baseSz = hlv switch { 1 => 20f, 2 => 16f, 3 => 13.5f, _ => 12f };
            baseBold = true;
            if (y > bodyTop) y += hlv == 1 ? 8f : 4f;
        }

        // Mark format size
        if (para.MarkFormat.Size is { } mSz and > 0)
            baseSz = mSz / 100f;

        // List prefix
        string? listPrefix = null;
        if (para.List is { Numbered: true } listRef)
        {
            var level = listRef.Level;
            var lvlDef = listRef.Numbering.Levels.ElementAtOrDefault(level);
            listPrefix = lvlDef != null && lvlDef.Bullet
                ? lvlDef.Text + " "
                : new string(' ', level * 4) + "• ";
        }

        // Indentation
        float indentL  = (para.Format.Left      ?? 0) * HwpToPt;
        float indentR  = (para.Format.Right     ?? 0) * HwpToPt;
        float firstAdj = (para.Format.FirstLine ?? 0) * HwpToPt;
        float effW = Math.Max(40f, w - indentL - indentR);

        // Collect render tokens from inlines
        var tokens = CollectTokens(para.Inlines, baseSz, baseBold, listPrefix,
            x + indentL, w, floatingImages, ref y, canvas, bodyTop, bodyBot, ensureRoom, ct);

        if (tokens.Count == 0)
        {
            // Empty paragraph — reserve one line of blank space
            ensureRoom(baseSz * ResolveLineSpacing(para.Format, baseSz));
            y += baseSz * ResolveLineSpacing(para.Format, baseSz);
        }
        else
        {
            var lines = WrapTokens(tokens, effW, firstAdj);
            bool isFirst = true;

            foreach (var line in lines)
            {
                float lineH = line.HeightPx * ResolveLineSpacing(para.Format, line.HeightPx);
                ensureRoom(lineH);

                float lineX = x + indentL + (isFirst ? Math.Max(-indentL + 8f, firstAdj) : 0f);
                isFirst = false;

                switch (para.Format.Align ?? HAlign.Left)
                {
                    case HAlign.Center:
                        lineX = x + (w - line.TotalWidth) / 2f;
                        break;
                    case HAlign.Right:
                        lineX = x + w - indentR - line.TotalWidth;
                        break;
                    case HAlign.Justify when !line.IsLast:
                        DrawJustified(canvas, line, lineX, y, lineH, effW);
                        y += lineH;
                        continue;
                }

                DrawLine(canvas, line, lineX, y, lineH);
                y += lineH;
            }
        }

        // Spacing after
        float after = (para.Format.After ?? 0) * HwpToPt;
        if (after > 0f)
            y += Math.Min(36f, after);
        else if (hlv > 0)
            y += 4f;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TABLE RENDERER
    // ─────────────────────────────────────────────────────────────────────────────

    private static void RenderTable(
        HTable tbl, SKCanvas canvas, SKDocument pdf,
        ref float y, ref int colIdx, ref int pageNum,
        float x, float w, float bodyTop, float bodyBot,
        Action<float> ensureRoom, Action advanceCol, Action ensurePage,
        float pgW, float pgH, HSection section,
        CancellationToken ct)
    {
        if (tbl.PageBreakBefore && y > bodyTop + 10f)
            advanceCol();

        y += 4f;

        int colCount = Math.Max(1,
            tbl.ColumnCount > 0 ? tbl.ColumnCount
            : tbl.Rows.Count   > 0 ? tbl.Rows.Max(r => r.Cells.Count) : 1);

        var colWidths = new float[colCount];

        if (tbl.ColumnWidths is { Length: > 0 } cw)
        {
            for (int i = 0; i < colCount; i++)
                colWidths[i] = i < cw.Length ? cw[i] * HwpToPt : w / colCount;
        }
        else if (tbl.RelativeWidths is { Length: > 0 } rw)
        {
            float total = (float)rw.Sum();
            for (int i = 0; i < colCount; i++)
                colWidths[i] = i < rw.Length ? (float)(rw[i] / total) * w : w / colCount;
        }
        else
        {
            float eq = w / colCount;
            for (int i = 0; i < colCount; i++) colWidths[i] = eq;
        }

        // Cell padding
        float padL = tbl.CellMargin?.Left   * HwpToPt ?? 4f;
        float padR = tbl.CellMargin?.Right  * HwpToPt ?? 4f;
        float padT = tbl.CellMargin?.Top    * HwpToPt ?? 3f;
        float padB = tbl.CellMargin?.Bottom * HwpToPt ?? 3f;

        foreach (var row in tbl.Rows)
        {
            ct.ThrowIfCancellationRequested();

            // Pre-compute cell lines for height estimation
            var cellData = new List<(List<RenderLine> Lines, float CellW)>();
            float cellSz = row.Header ? 9.5f : 9f;

            for (int c = 0; c < row.Cells.Count && c < colCount; c++)
            {
                var cell = row.Cells[c];
                float cw2 = colWidths[c] - padL - padR;
                var words = new List<RenderToken>();
                foreach (var b in cell.Blocks.OfType<HParagraph>())
                    words.AddRange(CollectTokens(b.Inlines, cellSz, row.Header, null, 0, cw2, null, ref y, canvas, 0, float.MaxValue, _ => { }, ct));
                cellData.Add((WrapTokens(words, Math.Max(10f, cw2), 0), cw2));
            }

            // Max row height
            float rowH = row.Height.HasValue && row.Height.Value > 0 ? row.Height.Value * HwpToPt : 0f;
            foreach (var (lines, _) in cellData)
            {
                float cellH = padT + padB + lines.Sum(l => l.HeightPx * DefaultLineSpacingPct);
                if (cellH > rowH) rowH = cellH;
            }
            rowH = Math.Max(14f, rowH);

            ensureRoom(rowH);

            float cx = x;
            for (int c = 0; c < row.Cells.Count && c < colCount; c++)
            {
                var cell = row.Cells[c];
                int span = Math.Min(cell.ColSpan > 1 ? cell.ColSpan : 1, colCount - c);
                float cw3 = 0f;
                for (int s = 0; s < span && c + s < colCount; s++) cw3 += colWidths[c + s];

                var cellRect = new SKRect(cx, y, cx + cw3, y + rowH);

                // Background
                SKColor bgColor = row.Header && string.IsNullOrWhiteSpace(cell.Fill)
                    ? new SKColor(230, 236, 244)
                    : ParseColor(cell.Fill, SKColors.White);

                if (row.Header || !string.IsNullOrWhiteSpace(cell.Fill))
                {
                    using var fillPaint = new SKPaint { Color = bgColor, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(cellRect, fillPaint);
                }

                // Borders
                DrawCellBorders(canvas, cellRect, cell.Borders ?? tbl.Borders);

                // Cell text
                if (c < cellData.Count)
                {
                    float cellY = y + padT;
                    foreach (var line in cellData[c].Lines)
                    {
                        float lineH = line.HeightPx * DefaultLineSpacingPct;
                        DrawLine(canvas, line, cx + padL, cellY, lineH);
                        cellY += lineH;
                    }
                }

                cx += cw3;
            }

            y += rowH;
        }

        y += 6f;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // TOKEN COLLECTOR  (inlines → flat RenderToken list)
    // ─────────────────────────────────────────────────────────────────────────────

    private static List<RenderToken> CollectTokens(
        IEnumerable<HInline> inlines,
        float defSz, bool defBold,
        string? listPrefix,
        float originX, float maxW,
        List<(SKCanvas, string, SKRect)>? floatingImages,
        ref float y, SKCanvas canvas,
        float bodyTop, float bodyBot,
        Action<float> ensureRoom,
        CancellationToken ct,
        SKColor? forcedColor = null)
    {
        var result = new List<RenderToken>();

        if (!string.IsNullOrEmpty(listPrefix))
        {
            var tf = ResolveTypeface(null, true, false);
            AppendTextTokens(result, listPrefix, tf, defSz, SKColors.Black, false, false, false, false);
        }

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText txt:
                {
                    if (string.IsNullOrEmpty(txt.Text)) continue;
                    var fmt = txt.Format;
                    float sz  = fmt.Size is { } s and > 0 ? s / 100f : defSz;
                    bool bold = fmt.Bold    ?? defBold;
                    bool itlc = fmt.Italic  ?? false;
                    bool ul   = fmt.Underline ?? false;
                    bool strk = fmt.Strike   ?? false;
                    bool sup  = fmt.Superscript ?? false;
                    bool sub  = fmt.Subscript   ?? false;
                    var color = forcedColor ?? ParseColor(fmt.Color, SKColors.Black);
                    var shade = ParseColor(fmt.Shade, SKColors.Transparent);
                    var tf    = ResolveTypeface(fmt.Font, bold, itlc);

                    // Superscript / subscript: shrink to 65 %
                    if (sup || sub) sz *= 0.65f;

                    AppendTextTokens(result, txt.Text, tf, sz, color, ul, strk, sup, sub, shade);
                    break;
                }

                case HLink link:
                {
                    var linkTokens = CollectTokens(link.Content, defSz, defBold, null,
                        originX, maxW, floatingImages, ref y, canvas, bodyTop, bodyBot, ensureRoom, ct,
                        new SKColor(0, 102, 204));
                    foreach (var lt in linkTokens)
                        result.Add(lt with { Underline = true, Color = new SKColor(0, 102, 204) });
                    break;
                }

                case HLineBreak:
                    result.Add(RenderToken.HardBreak(defSz));
                    break;

                case HTab:
                    result.Add(RenderToken.Tab(defSz));
                    break;

                case HImage img when img.Anchor == null:
                {
                    // Inline image — treated as a token with fixed width/height
                    float imgW = (img.Width.HasValue && img.Width.Value > 0) ? img.Width.Value * HwpToPt : Math.Min(maxW, 200f);
                    float imgH = (img.Height.HasValue && img.Height.Value > 0) ? img.Height.Value * HwpToPt : imgW * 0.75f;
                    result.Add(new RenderToken(string.Empty, null, defSz, SKColors.Transparent,
                        false, false, false, false, SKColors.Transparent,
                        imgW, 4f, true, img.Path, imgH));
                    break;
                }

                case HImage img when img.Anchor != null && floatingImages != null:
                {
                    // Floating image — record for deferred drawing
                    float imgW = (img.Width.HasValue && img.Width.Value > 0) ? img.Width.Value * HwpToPt : 100f;
                    float imgH = (img.Height.HasValue && img.Height.Value > 0) ? img.Height.Value * HwpToPt : 75f;
                    float fx   = originX + (img.Anchor.HorizontalOffset * HwpToPt);
                    float fy   = y       + (img.Anchor.VerticalOffset   * HwpToPt);
                    floatingImages.Add((canvas, img.Path, new SKRect(fx, fy, fx + imgW, fy + imgH)));
                    break;
                }

                case HTextBox txb:
                {
                    float tbW = txb.Width  * HwpToPt;
                    float tbH = txb.Height * HwpToPt;
                    ensureRoom(tbH + 6f);

                    // Draw box
                    var boxRect = new SKRect(originX, y, originX + tbW, y + tbH);
                    if (!string.IsNullOrWhiteSpace(txb.Fill))
                    {
                        using var fp = new SKPaint { Color = ParseColor(txb.Fill, SKColors.White), Style = SKPaintStyle.Fill };
                        if (txb.Shape == HShapeKind.Ellipse) canvas.DrawOval(boxRect, fp);
                        else canvas.DrawRect(boxRect, fp);
                    }
                    if (txb.Line.Style != HBorderStyle.None)
                    {
                        using var lp = new SKPaint { Color = ParseColor(txb.Line.Color, SKColors.Black), StrokeWidth = (float)txb.Line.WidthMm * 2.835f, Style = SKPaintStyle.Stroke };
                        if (txb.Shape == HShapeKind.Ellipse) canvas.DrawOval(boxRect, lp);
                        else canvas.DrawRect(boxRect, lp);
                    }

                    // Render text inside box
                    float tbPadL = txb.Padding.Left   * HwpToPt;
                    float tbPadT = txb.Padding.Top    * HwpToPt;
                    float tbInW  = Math.Max(10f, tbW - tbPadL - txb.Padding.Right * HwpToPt);
                    float innerY = y + tbPadT;
                    foreach (var b in txb.Blocks.OfType<HParagraph>())
                    {
                        var toks = CollectTokens(b.Inlines, defSz, defBold, null,
                            originX + tbPadL, tbInW, null, ref innerY, canvas,
                            y, y + tbH, _ => { }, ct);
                        var lines = WrapTokens(toks, tbInW, 0);
                        foreach (var line in lines)
                        {
                            float lh = line.HeightPx * DefaultLineSpacingPct;
                            DrawLine(canvas, line, originX + tbPadL, innerY, lh);
                            innerY += lh;
                        }
                        innerY += defSz * 0.3f;
                    }

                    y += tbH + 6f;
                    break;
                }

                case HShape shp:
                {
                    float shpW = shp.Width  * HwpToPt;
                    float shpH = shp.Height * HwpToPt;
                    ensureRoom(shpH + 4f);
                    var shpRect = new SKRect(originX, y, originX + shpW, y + shpH);

                    if (!string.IsNullOrWhiteSpace(shp.Fill))
                    {
                        using var fp = new SKPaint { Color = ParseColor(shp.Fill, SKColors.LightGray), Style = SKPaintStyle.Fill };
                        if (shp.Kind == HShapeKind.Ellipse) canvas.DrawOval(shpRect, fp);
                        else canvas.DrawRect(shpRect, fp);
                    }
                    if (shp.Line.Style != HBorderStyle.None)
                    {
                        using var lp = new SKPaint { Color = ParseColor(shp.Line.Color, SKColors.Black), StrokeWidth = (float)shp.Line.WidthMm * 2.835f, Style = SKPaintStyle.Stroke };
                        if (shp.Kind == HShapeKind.Ellipse) canvas.DrawOval(shpRect, lp);
                        else if (shp.Kind == HShapeKind.Line)
                        {
                            if (shp.FlipVertical)
                                canvas.DrawLine(originX, y, originX + shpW, y + shpH, lp);
                            else
                                canvas.DrawLine(originX, y + shpH, originX + shpW, y, lp);
                        }
                        else canvas.DrawRect(shpRect, lp);
                    }

                    y += shpH + 4f;
                    break;
                }

                case HField fld:
                {
                    var tf    = ResolveTypeface(null, false, false);
                    var text  = fld.Kind == HFieldKind.PageNumber ? "‹#›" : "‹N›";
                    float sz  = fld.Format.Size.HasValue && fld.Format.Size.Value > 0 ? fld.Format.Size.Value / 100f : defSz;
                    AppendTextTokens(result, text, tf, sz, SKColors.Gray, false, false, false, false);
                    break;
                }

                case HNote note:
                {
                    // Footnote marker — show superscript number
                    var tf = ResolveTypeface(null, false, false);
                    AppendTextTokens(result, note.Endnote ? "†" : "*", tf, defSz * 0.65f, SKColors.Gray, false, false, true, false);
                    break;
                }
            }
        }

        return result;
    }

    private static void AppendTextTokens(
        List<RenderToken> result, string text,
        SKTypeface tf, float sz, SKColor color,
        bool ul, bool strk, bool sup, bool sub,
        SKColor shade = default)
    {
        // Split on newlines first
        var lines2 = text.Split('\n');
        for (int li = 0; li < lines2.Length; li++)
        {
            if (li > 0) result.Add(RenderToken.HardBreak(sz));
            var segment = lines2[li].Replace("\r", "");

            // Split on spaces to get words
            var parts = segment.Split(' ');
            for (int pi = 0; pi < parts.Length; pi++)
            {
                var word = parts[pi];
                if (word.Length == 0)
                {
                    // Preserve spaces as trailing on previous token or as space token
                    if (result.Count > 0 && !result[^1].IsHardBreak && !result[^1].IsTab)
                    {
                        var prev = result[^1];
                        result[^1] = prev with { TrailingSpace = MeasureWord(" ", tf, sz) };
                    }
                    continue;
                }

                float wW = MeasureWord(word, tf, sz);
                float sp = pi < parts.Length - 1 ? MeasureWord(" ", tf, sz) : 0f;
                result.Add(new RenderToken(word, tf, sz, color, ul, strk, sup, sub, shade, wW, sp, false, null, sz));
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // WORD WRAPPER
    // ─────────────────────────────────────────────────────────────────────────────

    private static List<RenderLine> WrapTokens(List<RenderToken> tokens, float maxW, float firstLineAdjust)
    {
        var lines  = new List<RenderLine>();
        var cur    = new List<RenderToken>();
        float curW = 0f;
        bool isFirst = true;

        void Flush(bool isLast)
        {
            if (cur.Count > 0 || isLast)
            {
                float h = cur.Count > 0 ? cur.Max(t => t.IsImage ? t.ImageHeight : t.FontSize) : DefaultFontSizePt;
                lines.Add(new RenderLine([.. cur], curW, h, isLast));
                cur.Clear();
                curW  = 0f;
                isFirst = false;
            }
        }

        float effectiveMax = isFirst ? maxW - firstLineAdjust : maxW;

        foreach (var tok in tokens)
        {
            effectiveMax = isFirst ? maxW - firstLineAdjust : maxW;

            if (tok.IsHardBreak)
            {
                Flush(false);
                continue;
            }

            if (tok.IsTab)
            {
                float tabW = 36f; // 0.5 inch tab stop
                if (cur.Count > 0 && curW + tabW > effectiveMax)
                    Flush(false);
                cur.Add(tok with { Width = tabW, TrailingSpace = 0f });
                curW += tabW;
                continue;
            }

            float neededW = tok.Width + tok.TrailingSpace;

            if (cur.Count > 0 && curW + tok.Width > effectiveMax + 0.5f)
            {
                Flush(false);
                effectiveMax = maxW; // subsequent lines use full width
            }

            cur.Add(tok);
            curW += neededW;
        }

        Flush(true);
        return lines;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // LINE DRAWERS
    // ─────────────────────────────────────────────────────────────────────────────

    private static void DrawLine(SKCanvas canvas, RenderLine line, float x, float y, float lineH)
    {
        float baselineOffset = line.HeightPx * 0.82f; // ~82 % from top to baseline

        foreach (var tok in line.Words)
        {
            if (tok.IsHardBreak || tok.IsTab) { x += tok.Width; continue; }

            // Shade / highlight background
            if (tok.Shade != SKColors.Transparent && tok.Shade.Alpha > 0)
            {
                using var shadePaint = new SKPaint { Color = tok.Shade, Style = SKPaintStyle.Fill };
                canvas.DrawRect(new SKRect(x, y, x + tok.Width, y + lineH), shadePaint);
            }

            float tokenY = y + baselineOffset;
            float tokenSz = tok.FontSize;

            // Superscript rises up; subscript drops down
            if (tok.Superscript) tokenY -= line.HeightPx * 0.28f;
            else if (tok.Subscript) tokenY += line.HeightPx * 0.18f;

            if (tok.IsImage && tok.ImagePath != null)
            {
                // Draw inline image
                DrawInlineImage(canvas, tok.ImagePath, x, y, tok.Width, tok.ImageHeight);
                x += tok.Width + tok.TrailingSpace;
                continue;
            }

            if (tok.Typeface == null) { x += tok.Width + tok.TrailingSpace; continue; }

            using var font  = new SKFont(tok.Typeface, tokenSz);
            using var paint = new SKPaint { Color = tok.Color, IsAntialias = true };

            canvas.DrawText(tok.Text, x, tokenY, font, paint);

            if (tok.Underline)
            {
                using var up = new SKPaint { Color = tok.Color, StrokeWidth = Math.Max(0.5f, tokenSz * 0.06f), IsAntialias = true };
                canvas.DrawLine(x, tokenY + 2f, x + tok.Width, tokenY + 2f, up);
            }

            if (tok.Strike)
            {
                using var sp = new SKPaint { Color = tok.Color, StrokeWidth = Math.Max(0.5f, tokenSz * 0.06f), IsAntialias = true };
                float strikeY = tokenY - tokenSz * 0.28f;
                canvas.DrawLine(x, strikeY, x + tok.Width, strikeY, sp);
            }

            x += tok.Width + tok.TrailingSpace;
        }
    }

    private static void DrawJustified(SKCanvas canvas, RenderLine line, float x, float y, float lineH, float maxW)
    {
        int wordCount = line.Words.Count(t => !t.IsHardBreak && !t.IsTab);
        if (wordCount <= 1) { DrawLine(canvas, line, x, y, lineH); return; }

        float totalWordW = line.Words.Where(t => !t.IsHardBreak && !t.IsTab).Sum(t => t.Width);
        float totalGap   = maxW - totalWordW;
        float gapEach    = totalGap / (wordCount - 1);

        float cx = x;
        int drawn = 0;
        foreach (var tok in line.Words)
        {
            if (tok.IsHardBreak || tok.IsTab) continue;
            DrawLine(canvas, new RenderLine([tok], tok.Width, line.HeightPx, false), cx, y, lineH);
            cx += tok.Width + (drawn < wordCount - 1 ? gapEach : 0f);
            drawn++;
        }
    }

    private static void DrawInlineImage(SKCanvas canvas, string path, float x, float y, float w, float h)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap == null) return;

            var destRect = new SKRect(x, y, x + w, y + h);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(bitmap, destRect, paint);
        }
        catch { /* image load failure is non-fatal */ }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HEADERS / FOOTERS
    // ─────────────────────────────────────────────────────────────────────────────

    private static void DrawHeaderFooterBlocks(
        SKCanvas canvas, IReadOnlyList<HHeaderFooter> hfs,
        float pgW, float x, float y, float w,
        int pageNum, bool suppress)
    {
        if (suppress || hfs.Count == 0) return;

        var target = hfs.FirstOrDefault(h => h.Pages == HPageType.Both)
                  ?? hfs.FirstOrDefault(h => h.Pages == (pageNum % 2 == 0 ? HPageType.Even : HPageType.Odd))
                  ?? hfs[0];

        float hfY = y;
        foreach (var block in target.Blocks.OfType<HParagraph>())
        {
            var toks = CollectTokens(block.Inlines, 9f, false, null, x, w, null, ref hfY, canvas, y, y + 30f, _ => { }, default);
            var lines = WrapTokens(toks, w, 0);
            foreach (var line in lines)
            {
                float lh = line.HeightPx * DefaultLineSpacingPct;
                DrawLine(canvas, line, x, hfY, lh);
                hfY += lh;
            }
        }
    }

    private static void DrawPageNumber(SKCanvas canvas, float pgW, float y, int pageNum)
    {
        using var tf    = ResolveTypeface(null, false, false);
        using var font  = new SKFont(tf, 9f);
        using var paint = new SKPaint { Color = new SKColor(140, 140, 140), IsAntialias = true };
        var text  = pageNum.ToString();
        float tw  = font.MeasureText(text, paint);
        canvas.DrawText(text, (pgW - tw) / 2f, y, font, paint);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // CELL BORDER DRAWING
    // ─────────────────────────────────────────────────────────────────────────────

    private static void DrawCellBorders(SKCanvas canvas, SKRect rect, HBorders? borders)
    {
        var b = borders ?? HBorders.Grid;
        DrawBorderLine(canvas, b.Top,    rect.Left,  rect.Top,    rect.Right, rect.Top);
        DrawBorderLine(canvas, b.Bottom, rect.Left,  rect.Bottom, rect.Right, rect.Bottom);
        DrawBorderLine(canvas, b.Left,   rect.Left,  rect.Top,    rect.Left,  rect.Bottom);
        DrawBorderLine(canvas, b.Right,  rect.Right, rect.Top,    rect.Right, rect.Bottom);
    }

    private static void DrawBorderLine(SKCanvas canvas, HBorder border, float x1, float y1, float x2, float y2)
    {
        if (border.Style == HBorderStyle.None) return;
        using var paint = new SKPaint
        {
            Color       = ParseColor(border.Color, new SKColor(180, 185, 190)),
            StrokeWidth = Math.Max(0.5f, (float)border.WidthMm * 2.835f),
            IsAntialias = true,
            PathEffect  = border.Style == HBorderStyle.Dash ? SKPathEffect.CreateDash([6f, 3f], 0f)
                        : border.Style == HBorderStyle.Dot  ? SKPathEffect.CreateDash([1.5f, 2f], 0f)
                        : null,
        };
        canvas.DrawLine(x1, y1, x2, y2, paint);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // LINE-SPACING HELPER
    // ─────────────────────────────────────────────────────────────────────────────

    private static float ResolveLineSpacing(HParaFormat fmt, float fontSizePt)
    {
        if (fmt.LineSpacing is not { } ls) return DefaultLineSpacingPct;

        return ls.Kind switch
        {
            HLineSpacingKind.Percent => ls.Value / 100f,
            HLineSpacingKind.Fixed   => fontSizePt > 0 ? (ls.Value * HwpToPt) / fontSizePt : DefaultLineSpacingPct,
            HLineSpacingKind.AtLeast => fontSizePt > 0 ? Math.Max(DefaultLineSpacingPct, (ls.Value * HwpToPt) / fontSizePt) : DefaultLineSpacingPct,
            _                        => DefaultLineSpacingPct,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // FONT / COLOR HELPERS
    // ─────────────────────────────────────────────────────────────────────────────

    private static float MeasureWord(string text, SKTypeface tf, float size)
    {
        using var font = new SKFont(tf, size);
        return font.MeasureText(text);
    }

    private static SKTypeface ResolveTypeface(string? name, bool bold, bool italic)
    {
        var weight = bold   ? SKFontStyleWeight.Bold   : SKFontStyleWeight.Normal;
        var slant  = italic ? SKFontStyleSlant.Italic  : SKFontStyleSlant.Upright;
        var style  = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);

        if (!string.IsNullOrWhiteSpace(name))
        {
            var tf = SKTypeface.FromFamilyName(name, style);
            if (tf != null && tf.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return tf;
        }

        return SKTypeface.FromFamilyName("Segoe UI", style)
            ?? SKTypeface.FromFamilyName("Arial", style)
            ?? SKTypeface.FromFamilyName("Calibri", style)
            ?? SKTypeface.Default;
    }

    private static SKColor ParseColor(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 && uint.TryParse(clean, NumberStyles.HexNumber, null, out var rgb))
            return new SKColor((byte)(rgb >> 16 & 0xFF), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));
        if (clean.Length == 8 && uint.TryParse(clean, NumberStyles.HexNumber, null, out var argb))
            return new SKColor((byte)(argb >> 16 & 0xFF), (byte)(argb >> 8 & 0xFF), (byte)(argb & 0xFF), (byte)(argb >> 24 & 0xFF));
        return fallback;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // DATA TYPES
    // ─────────────────────────────────────────────────────────────────────────────

    private sealed record RenderToken(
        string Text,
        SKTypeface? Typeface,
        float FontSize,
        SKColor Color,
        bool Underline,
        bool Strike,
        bool Superscript,
        bool Subscript,
        SKColor Shade,
        float Width,
        float TrailingSpace,
        bool IsImage,
        string? ImagePath,
        float ImageHeight)
    {
        public bool IsHardBreak => Width < 0f;
        public bool IsTab       => Width == -2f;

        public static RenderToken HardBreak(float sz) =>
            new(string.Empty, null, sz, SKColors.Transparent, false, false, false, false, SKColors.Transparent, -1f, 0f, false, null, sz);

        public static RenderToken Tab(float sz) =>
            new(string.Empty, null, sz, SKColors.Transparent, false, false, false, false, SKColors.Transparent, -2f, 0f, false, null, sz);
    }

    private sealed record RenderLine(IReadOnlyList<RenderToken> Words, float TotalWidth, float HeightPx, bool IsLast)
    {
        public float Max(Func<RenderToken, float> sel) =>
            Words.Count > 0 ? Words.Max(sel) : DefaultFontSizePt;
    }
}
