// In-page pre-split script of the pre-split layout experiment.
//
// Loaded by the PDF page only (the semantic source page has no script). Before
// the pagination library lays the block out, it copies the detail into an
// off-screen measuring container of the content width, measures every item
// row and, in "split" mode, replaces each row taller than the trigger by
// continuation parts that each fit one page:
//
// - the text cells (description, and note in the five-column geometry) are cut
//   at line boundaries, each cell at its own lines, so that every part's
//   re-measured height stays within the part limit;
// - the sequence and date stay on the first part, the amount on the last part;
//   every other part has an empty amount cell (the unchanged accounting script
//   writes the "(continues)" mark there);
// - parts 2 ... n carry the class "continuation" (a forced page break before
//   them) and all parts keep the row's data-seq and data-amount.
//
// Text moves only through text nodes and textContent, never through markup.
// The measuring container is removed before the pagination library starts.
// window.__preSplit({ trigger, partLimit, mode }) with mode "measure-only" or
// "split"; the outcome is in window.__splitResult.
(function () {
    "use strict";

    const MM = 96 / 25.4;
    // Declared content area: A4 297 mm minus the 15 mm top and 18 mm bottom margins.
    const CONTENT_HEIGHT = 264 * MM;
    // Declared content width: 210 mm minus the 12 mm left and right margins.
    const CONTENT_WIDTH_MM = 186;
    const TEXT_CELLS = ["desc", "note"];

    function now() {
        return performance.now();
    }

    function round(value, digits) {
        const f = Math.pow(10, digits);
        return Math.round(value * f) / f;
    }

    // The single text node of a cell of the measuring copy (adjacent text nodes merged).
    function textNode(cell) {
        cell.normalize();
        const node = cell.firstChild;
        return node && node.nodeType === Node.TEXT_NODE ? node : null;
    }

    // Line box of the first character at or after offset o that has a box, or null.
    function boxAt(node, o, range) {
        const length = node.length;
        for (let i = o; i < length; i++) {
            range.setStart(node, i);
            range.setEnd(node, i + 1);
            const rects = range.getClientRects();
            for (let k = 0; k < rects.length; k++) {
                if (rects[k].height > 0) {
                    return { top: rects[k].top, bottom: rects[k].bottom };
                }
            }
        }
        return null;
    }

    // Smallest offset in [lo, hi) whose line ends below limitBottom; hi when none does.
    function firstBeyond(node, lo, hi, limitBottom, range) {
        let a = lo;
        let b = hi;
        while (a < b) {
            const m = Math.floor((a + b) / 2);
            const box = boxAt(node, m, range);
            if (box === null || box.bottom <= limitBottom + 0.01) {
                a = m + 1;
            } else {
                b = m;
            }
        }
        return a;
    }

    // Start of the line that holds offset c: the smallest offset in [lo, c] on the same line.
    function lineStart(node, lo, c, range) {
        const top = boxAt(node, c, range).top;
        let a = lo;
        let b = c;
        while (a < b) {
            const m = Math.floor((a + b) / 2);
            const box = boxAt(node, m, range);
            if (box !== null && box.top >= top - 0.5) {
                b = m;
            } else {
                a = m + 1;
            }
        }
        return a;
    }

    function verticalPadding(cell) {
        const style = getComputedStyle(cell);
        return parseFloat(style.paddingTop) + parseFloat(style.paddingBottom) +
            parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    }

    // L1: cut offsets of one cell at line boundaries, from the cell laid out once at its width.
    function cutsByLines(cell, limitPx) {
        const node = textNode(cell);
        if (node === null) {
            return [0];
        }
        const length = node.length;
        const range = document.createRange();
        const padding = verticalPadding(cell);
        const lineHeight = parseFloat(getComputedStyle(cell).lineHeight);
        const cuts = [0];
        let start = 0;
        while (start < length) {
            const first = boxAt(node, start, range);
            if (first === null) {
                break;
            }
            // Character boxes are shorter than line boxes by the leading, above and below.
            const halfLeading = isFinite(lineHeight) ? Math.max(0, (lineHeight - (first.bottom - first.top)) / 2) : 0;
            const limitBottom = first.top - halfLeading + limitPx - padding - halfLeading;
            const beyond = firstBeyond(node, start, length, limitBottom, range);
            if (beyond >= length) {
                break;
            }
            let next = lineStart(node, start, beyond, range);
            if (next <= start) {
                // A single line taller than the limit cannot happen at the allowed limits; cut after it.
                next = beyond > start ? beyond : start + 1;
            }
            cuts.push(next);
            start = next;
        }
        range.detach();
        return cuts;
    }

    // L2: cut offsets of one cell by measuring candidate parts, over whitespace boundaries.
    function cutsByMeasure(cell, text, limitPx, probeRow) {
        const probe = probeRow.querySelector(":scope > ." + cell.classList[0]);
        const boundaries = [];
        for (let i = 0; i < text.length; i++) {
            if (/\s/.test(text[i])) {
                boundaries.push(i + 1);
            }
        }
        boundaries.push(text.length);
        const cuts = [0];
        let start = 0;
        while (start < text.length) {
            probe.textContent = text.slice(start);
            if (probeRow.getBoundingClientRect().height <= limitPx) {
                break;
            }
            let lo = 0;
            while (lo < boundaries.length && boundaries[lo] <= start) {
                lo++;
            }
            let hi = boundaries.length - 1;
            let best = -1;
            while (lo <= hi) {
                const m = Math.floor((lo + hi) / 2);
                probe.textContent = text.slice(start, boundaries[m]);
                if (probeRow.getBoundingClientRect().height <= limitPx) {
                    best = m;
                    lo = m + 1;
                } else {
                    hi = m - 1;
                }
            }
            if (best < 0) {
                throw new Error("No whitespace boundary gives a part within the limit.");
            }
            cuts.push(boundaries[best]);
            start = boundaries[best];
        }
        probe.textContent = "";
        return cuts;
    }

    // Builds the parts of one row: shallow copies of the row and its cells, text set through textContent.
    function buildParts(row, texts, count) {
        const parts = [];
        const cells = Array.from(row.children);
        for (let i = 0; i < count; i++) {
            const part = row.cloneNode(false);
            part.classList.add("part");
            if (i > 0) {
                part.classList.add("continuation");
            }
            part.setAttribute("data-part", (i + 1) + "/" + count);
            for (const cell of cells) {
                const copy = cell.cloneNode(false);
                const name = cell.classList[0];
                let text = "";
                if (TEXT_CELLS.indexOf(name) >= 0) {
                    text = texts[name][i] || "";
                } else if (name === "amount") {
                    text = i === count - 1 ? cell.textContent : "";
                } else {
                    text = i === 0 ? cell.textContent : "";
                }
                if (text.length > 0) {
                    copy.appendChild(document.createTextNode(text));
                }
                part.appendChild(copy);
            }
            parts.push(part);
        }
        return parts;
    }

    function slices(text, cuts) {
        const out = [];
        for (let i = 0; i < cuts.length; i++) {
            out.push(text.slice(cuts[i], i + 1 < cuts.length ? cuts[i + 1] : text.length));
        }
        return out;
    }

    window.__preSplit = async function (options) {
        const result = {
            mode: options.mode,
            trigger: options.trigger,
            partLimit: options.partLimit,
            contentHeightPx: round(CONTENT_HEIGHT, 3),
            rowsMeasured: 0,
            maxRowFraction: null,
            measured: [],
            splitRows: [],
            partsTotal: 0,
            selfCheck: null,
            overLimitParts: 0,
            measureMs: null,
            splitMs: null,
            error: null
        };
        window.__splitResult = result;
        let box = null;
        try {
            await document.fonts.ready;
            const t0 = now();
            const grid = document.querySelector(".rows-grid");
            box = document.createElement("div");
            box.setAttribute("aria-hidden", "true");
            box.style.cssText = "position:absolute;left:0;top:0;width:" + CONTENT_WIDTH_MM + "mm;visibility:hidden;";
            const copy = grid.cloneNode(true);
            box.appendChild(copy);
            document.body.appendChild(box);
            const realRows = Array.from(grid.querySelectorAll(":scope > .item[data-seq]"));
            const copyRows = Array.from(copy.querySelectorAll(":scope > .item[data-seq]"));
            const heights = copyRows.map(function (r) {
                return r.getBoundingClientRect().height;
            });
            result.rowsMeasured = heights.length;
            let max = 0;
            heights.forEach(function (h, i) {
                const fraction = h / CONTENT_HEIGHT;
                max = Math.max(max, fraction);
                if (fraction > 0.5 || copyRows[i].dataset.seq === "50") {
                    result.measured.push({ seq: Number(copyRows[i].dataset.seq), heightPx: round(h, 2), fraction: round(fraction, 4) });
                }
            });
            result.maxRowFraction = round(max, 4);
            result.measureMs = round(now() - t0, 1);

            const t1 = now();
            let selfCheck = true;
            if (options.mode === "split") {
                const triggerPx = options.trigger * CONTENT_HEIGHT;
                const limitPx = options.partLimit * CONTENT_HEIGHT;
                const replacements = [];
                for (let i = 0; i < copyRows.length; i++) {
                    if (!(heights[i] > triggerPx)) {
                        continue;
                    }
                    const row = copyRows[i];
                    const record = {
                        seq: Number(row.dataset.seq),
                        heightPx: round(heights[i], 2),
                        fraction: round(heights[i] / CONTENT_HEIGHT, 4),
                        method: "L1",
                        parts: 0,
                        partHeightsPx: [],
                        maxPartFraction: null,
                        cuts: {},
                        selfCheck: true
                    };
                    const originals = {};
                    const texts = {};
                    let cutsOf = {};
                    for (const name of TEXT_CELLS) {
                        const cell = row.querySelector(":scope > ." + name);
                        if (cell) {
                            originals[name] = cell.textContent;
                            cutsOf[name] = cutsByLines(cell, limitPx);
                        }
                    }

                    function assemble() {
                        let count = 1;
                        for (const name of Object.keys(cutsOf)) {
                            texts[name] = slices(originals[name], cutsOf[name]);
                            count = Math.max(count, texts[name].length);
                        }
                        return count;
                    }

                    let count = assemble();
                    let parts = buildParts(realRows[i], texts, count);
                    row.after.apply(row, parts);
                    let partHeights = parts.map(function (p) {
                        return p.getBoundingClientRect().height;
                    });
                    if (partHeights.some(function (h) { return h > limitPx + 0.5; })) {
                        // L1 gave a part above the limit after its own re-measure: cut this row by measurement (L2).
                        parts.forEach(function (p) { p.remove(); });
                        record.method = "L2";
                        record.l1PartHeightsPx = partHeights.map(function (h) { return round(h, 2); });
                        const probeRow = buildParts(realRows[i], {}, 1)[0];
                        probeRow.querySelectorAll(":scope > div").forEach(function (c) { c.textContent = ""; });
                        row.after(probeRow);
                        cutsOf = {};
                        for (const name of Object.keys(originals)) {
                            cutsOf[name] = cutsByMeasure(row.querySelector(":scope > ." + name), originals[name], limitPx, probeRow);
                        }
                        probeRow.remove();
                        count = assemble();
                        parts = buildParts(realRows[i], texts, count);
                        row.after.apply(row, parts);
                        partHeights = parts.map(function (p) {
                            return p.getBoundingClientRect().height;
                        });
                    }

                    for (const name of Object.keys(originals)) {
                        const joined = parts.map(function (p) {
                            const c = p.querySelector(":scope > ." + name);
                            return c ? c.textContent : "";
                        }).join("");
                        if (joined !== originals[name]) {
                            record.selfCheck = false;
                            selfCheck = false;
                        }
                        record.cuts[name] = cutsOf[name];
                    }
                    record.parts = count;
                    record.partHeightsPx = partHeights.map(function (h) { return round(h, 2); });
                    record.maxPartFraction = round(Math.max.apply(null, partHeights) / CONTENT_HEIGHT, 4);
                    result.overLimitParts += partHeights.filter(function (h) { return h > limitPx + 0.5; }).length;
                    result.partsTotal += count;
                    result.splitRows.push(record);
                    replacements.push({ real: realRows[i], parts: parts });
                }
                // Move the measured parts into the page, in place of the rows they replace.
                for (const r of replacements) {
                    r.real.replaceWith.apply(r.real, r.parts);
                }
            }
            result.selfCheck = selfCheck;
            box.remove();
            box = null;
            result.splitMs = round(now() - t1, 1);
        } catch (e) {
            result.error = String(e && e.stack ? e.stack : e);
            if (box !== null) {
                box.remove();
            }
            throw e;
        }
    };
})();
