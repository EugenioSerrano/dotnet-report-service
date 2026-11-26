// In-page accounting script of the page-accounting print.
//
// The service serves it after the composed block and the page rule, with Paged.js
// 0.4.3's auto-start switched off. It does three things, in this order:
//
// 1. Split before layout. It measures every detail row at the content width with the
//    document's own stylesheets and fonts, cuts any row taller than one content area
//    into continuation parts of at most 0.90 content area at line boundaries (each
//    text cell at its own lines), moves the amount to the row's last part and leaves
//    the fixed "(continues)" mark on every earlier part's own amount cell. Each part
//    after the first starts a new page. Only then does it start Paged.js.
//
// 2. Account after layout. Once Paged.js has laid the block out, it attributes every
//    row fragment to its page by its row ordinal, computes each page's subtotal from
//    the rows that ended on it, and the brought/carried-forward chain from the block's
//    opening value the service recorded, in scaled integers (BigInt), never floating
//    point. It writes the three values into the reserved band, writes each final
//    group's total into its own cell, and marks the continuation parts.
//
// 3. Signal. It sets document.documentElement.dataset.layout to "done" only when every
//    page has its band, every final group has its total and the in-render guard holds;
//    otherwise to "failed" with a reason token, and it publishes the guard report the
//    print reads. The report is used for the guard only - never as an expected
//    accounting value, which comes from the block's source data and the partition.
//
// The values are written with exactly the declared scale and no group separator, in
// the block's presentation culture, so the service's verifier reads back the number
// the page shows. The rectangle is the template's own, in millimetres, inside the
// bottom margin band; the band is written after layout, so it can never move a row.
(function () {
    "use strict";

    var MM = 96 / 25.4;
    var TRIGGER = 1.00;
    var PART_LIMIT = 0.90;
    var MAX_PARTS = 220;
    var MARK = "(continues)";

    var guard = {
        phase: "start",
        pages: 0,
        rows: 0,
        splitRows: 0,
        maxParts: 0,
        partOverLimit: 0,
        unfinishedLastPage: false,
        accountingError: null,
        error: null
    };
    window.__layoutGuard = guard;

    function fail(reason, error) {
        document.documentElement.dataset.layout = "failed";
        document.documentElement.dataset.layoutReason = reason;
        guard.error = error ? String(error && error.stack ? error.stack : error) : reason;
    }

    function facts() {
        var root = document.documentElement;
        var text = root.getAttribute("data-accounting");
        if (!text) {
            return null;
        }
        try {
            return JSON.parse(text);
        } catch (e) {
            return null;
        }
    }

    function style() {
        var element = document.createElement("style");
        element.setAttribute("data-accounting-styles", "true");
        element.appendChild(document.createTextNode(
            "[data-accounting-continuation]{break-before:page;}\n" +
            ".pagedjs_page{position:relative;}\n" +
            ".accounting-band{position:absolute;overflow:hidden;}\n" +
            ".accounting-band>div{white-space:nowrap;}\n"));
        document.head.appendChild(element);
    }

    // ---- scaled integers ---------------------------------------------------------

    function toUnits(text, scale) {
        var value = String(text === null || text === undefined ? "" : text).trim();
        var negative = value.charAt(0) === "-";
        if (negative) {
            value = value.slice(1);
        }
        var at = value.indexOf(".");
        var integer = at < 0 ? value : value.slice(0, at);
        var fraction = at < 0 ? "" : value.slice(at + 1);
        if (integer.length === 0 || !/^\d+$/.test(integer) || !/^\d*$/.test(fraction)) {
            throw new Error("unusable amount text");
        }
        while (fraction.length < scale) {
            fraction += "0";
        }
        var units = BigInt(integer) * (BigInt(10) ** BigInt(scale)) + BigInt(fraction.slice(0, scale) || "0");
        return negative ? -units : units;
    }

    function unitsToText(units, scale) {
        var negative = units < 0n;
        var abs = negative ? -units : units;
        var factor = BigInt(10) ** BigInt(scale);
        var integer = (abs / factor).toString();
        var text = integer;
        if (scale > 0) {
            var fraction = (abs % factor).toString();
            while (fraction.length < scale) {
                fraction = "0" + fraction;
            }
            text = integer + "." + fraction;
        }
        return (negative ? "-" : "") + text;
    }

    function format(units, options) {
        // The exact text is written with the culture's decimal separator and no group
        // separator; the number never passes through binary floating point.
        var text = unitsToText(units, options.scale);
        var separator = options.decimalSeparator && options.decimalSeparator.length > 0 ? options.decimalSeparator : ".";
        return text.replace(".", separator);
    }

    // ---- split before layout -----------------------------------------------------

    function contentBox(options) {
        return {
            width: options.contentWidthMm * MM,
            height: options.contentHeightMm * MM
        };
    }

    function rowsOf(root) {
        return Array.prototype.slice.call(root.querySelectorAll("[data-seq]"));
    }

    function rowRoot(row) {
        var node = row;
        while (node.parentElement && !node.parentElement.hasAttribute("data-accounting-measure")) {
            if (node.parentElement.hasAttribute("data-accounting-grid")) {
                break;
            }
            node = node.parentElement;
        }
        return node;
    }

    function textCells(row) {
        var cells = [];
        var children = row.children;
        for (var i = 0; i < children.length; i++) {
            var cell = children[i];
            if (cell.getAttribute("data-role") === "amount" || cell.getAttribute("data-role") === "group-total") {
                continue;
            }
            if (cell.textContent && cell.textContent.length > 0) {
                cells.push(cell);
            }
        }
        return cells;
    }

    function singleTextNode(cell) {
        cell.normalize();
        var node = cell.firstChild;
        return node && node.nodeType === Node.TEXT_NODE ? node : null;
    }

    function boxAt(node, offset, range) {
        for (var i = offset; i < node.length; i++) {
            range.setStart(node, i);
            range.setEnd(node, i + 1);
            var rects = range.getClientRects();
            for (var k = 0; k < rects.length; k++) {
                if (rects[k].height > 0) {
                    return { top: rects[k].top, bottom: rects[k].bottom };
                }
            }
        }
        return null;
    }

    function firstBeyond(node, lo, hi, limitBottom, range) {
        var a = lo;
        var b = hi;
        while (a < b) {
            var m = Math.floor((a + b) / 2);
            var box = boxAt(node, m, range);
            if (box === null || box.bottom <= limitBottom + 0.01) {
                a = m + 1;
            } else {
                b = m;
            }
        }
        return a;
    }

    function lineStart(node, lo, at, range) {
        var box = boxAt(node, at, range);
        if (box === null) {
            return at;
        }
        var a = lo;
        var b = at;
        while (a < b) {
            var m = Math.floor((a + b) / 2);
            var candidate = boxAt(node, m, range);
            if (candidate !== null && candidate.top >= box.top - 0.5) {
                b = m;
            } else {
                a = m + 1;
            }
        }
        return a;
    }

    function verticalExtras(cell) {
        var style = getComputedStyle(cell);
        return parseFloat(style.paddingTop) + parseFloat(style.paddingBottom) +
            parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
    }

    function cutsByLines(cell, limitPx) {
        var node = singleTextNode(cell);
        if (node === null) {
            return [0];
        }
        var length = node.length;
        var range = document.createRange();
        var padding = verticalExtras(cell);
        var lineHeight = parseFloat(getComputedStyle(cell).lineHeight);
        var cuts = [0];
        var start = 0;
        while (start < length) {
            var first = boxAt(node, start, range);
            if (first === null) {
                break;
            }
            var halfLeading = isFinite(lineHeight) ? Math.max(0, (lineHeight - (first.bottom - first.top)) / 2) : 0;
            var limitBottom = first.top - halfLeading + limitPx - padding - halfLeading;
            var beyond = firstBeyond(node, start, length, limitBottom, range);
            if (beyond >= length) {
                break;
            }
            var next = lineStart(node, start, beyond, range);
            if (next <= start) {
                next = beyond > start ? beyond : start + 1;
            }
            cuts.push(next);
            start = next;
        }
        range.detach();
        return cuts;
    }

    function slices(text, cuts) {
        var out = [];
        for (var i = 0; i < cuts.length; i++) {
            out.push(text.slice(cuts[i], i + 1 < cuts.length ? cuts[i + 1] : text.length));
        }
        return out;
    }

    function cutsByMeasure(cell, text, limitPx, probeRow) {
        var role = cell.getAttribute("data-role") === "amount" ? "amount" : "text";
        var probe = probeRow.querySelector('[data-accounting-probe="' + role + '"]');
        if (!probe) {
            return [0, text.length];
        }
        var boundaries = [];
        for (var i = 0; i < text.length; i++) {
            if (/\s/.test(text.charAt(i))) {
                boundaries.push(i + 1);
            }
        }
        boundaries.push(text.length);
        var cuts = [0];
        var start = 0;
        while (start < text.length) {
            probe.textContent = text.slice(start);
            if (probeRow.getBoundingClientRect().height <= limitPx) {
                break;
            }
            var lo = 0;
            while (lo < boundaries.length && boundaries[lo] <= start) {
                lo++;
            }
            var hi = boundaries.length - 1;
            var best = -1;
            while (lo <= hi) {
                var m = Math.floor((lo + hi) / 2);
                probe.textContent = text.slice(start, boundaries[m]);
                if (probeRow.getBoundingClientRect().height <= limitPx) {
                    best = m;
                    lo = m + 1;
                } else {
                    hi = m - 1;
                }
            }
            if (best < 0) {
                throw new Error("no boundary fits the part limit");
            }
            cuts.push(boundaries[best]);
            start = boundaries[best];
        }
        probe.textContent = "";
        return cuts;
    }

    function cloneCellShallow(cell) {
        var copy = cell.cloneNode(false);
        copy.removeAttribute("data-accounting-probe");
        return copy;
    }

    function buildParts(row, texts, count, amountCell, amountText, totals) {
        var parts = [];
        var cells = Array.prototype.slice.call(row.children);
        for (var i = 0; i < count; i++) {
            var part = row.cloneNode(false);
            part.setAttribute("data-accounting-part", (i + 1) + "/" + count);
            if (i > 0) {
                part.setAttribute("data-accounting-continuation", "true");
            // The break is inline: the injected stylesheet is not the only carrier of it.
            part.style.breakBefore = "page";
            }
            for (var c = 0; c < cells.length; c++) {
                var cell = cells[c];
                var copy = cloneCellShallow(cell);
                var role = cell.getAttribute("data-role") || "";
                var text = "";
                if (role === "amount") {
                    text = i === count - 1 ? amountText : "";
                } else if (Object.prototype.hasOwnProperty.call(texts, String(c))) {
                    text = texts[String(c)][i] || "";
                } else {
                    text = i === 0 ? cell.textContent : "";
                }
                if (totals && totals[c]) {
                    text = totals[c];
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

    function splitRows(options) {
        var grid = null;
        var rows = rowsOf(document.body);
        if (rows.length === 0) {
            return;
        }
        var parent = rows[0].parentElement;
        grid = parent;
        var box = document.createElement("div");
        box.setAttribute("data-accounting-measure", "true");
        box.setAttribute("aria-hidden", "true");
        box.style.cssText = "position:absolute;left:0;top:0;visibility:hidden;width:" + options.contentWidthMm + "mm;";
        var copy = grid.cloneNode(true);
        box.appendChild(copy);
        document.body.appendChild(box);

        try {
            var content = contentBox(options);
            var triggerPx = TRIGGER * content.height;
            var limitPx = PART_LIMIT * content.height;
            var measured = rowsOf(copy);
            var original = rowsOf(grid);
            if (measured.length !== original.length) {
                throw new Error("the measuring copy does not hold the block's rows");
            }

            var heights = measured.map(function (row) {
                return row.getBoundingClientRect().height;
            });
            guard.rows = heights.length;

            var replacements = [];
            for (var i = 0; i < heights.length; i++) {
                if (!(heights[i] > triggerPx)) {
                    continue;
                }

                var row = original[i];
                var copyRow = measured[i];
                var amountCell = row.querySelector('[data-role="amount"]');
                var amountText = amountCell ? amountCell.textContent : "";
                var texts = {};
                var cutsOf = {};
                var cells = textCells(row);
                for (var c = 0; c < cells.length; c++) {
                    var cell = cells[c];
                    var index = Array.prototype.indexOf.call(row.children, cell);
                    texts[String(index)] = cell.textContent;
                    cutsOf[String(index)] = cutsByLines(copyRow.children[index], limitPx);
                }

                function assemble() {
                    var count = 1;
                    var parts = {};
                    for (var key in texts) {
                        if (Object.prototype.hasOwnProperty.call(texts, key)) {
                            parts[key] = slices(texts[key], cutsOf[key]);
                            count = Math.max(count, parts[key].length);
                        }
                    }
                    return { count: count, parts: parts };
                }

                var assembled = assemble();
                var parts = buildParts(row, assembled.parts, assembled.count, amountCell, amountText, null);
                var probeParent = copyRow.parentElement;
                for (var p = 0; p < parts.length; p++) {
                    probeParent.insertBefore(parts[p].cloneNode(true), copyRow);
                }

                var heightsOfParts = parts.map(function (part) {
                    var clone = part.cloneNode(true);
                    probeParent.insertBefore(clone, copyRow);
                    var height = clone.getBoundingClientRect().height;
                    clone.remove();
                    if (height > limitPx + 0.5) {
                        guard.partOverLimit++;
                    }
                    return height;
                });

                if (heightsOfParts.some(function (height) { return height > limitPx + 0.5; })) {
                    // The line cuts did not fit every part: cut this row by measurement.
                    var probeRow = row.cloneNode(false);
                    probeRow.setAttribute("data-accounting-probe", "row");
                    for (var probeIndex = 0; probeIndex < row.children.length; probeIndex++) {
                        var probeCell = cloneCellShallow(row.children[probeIndex]);
                        probeCell.setAttribute("data-accounting-probe",
                            row.children[probeIndex].getAttribute("data-role") === "amount" ? "amount" : "text");
                        probeRow.appendChild(probeCell);
                    }
                    probeParent.insertBefore(probeRow, copyRow);
                    for (var key2 in texts) {
                        if (Object.prototype.hasOwnProperty.call(texts, key2)) {
                            var cellIndex = Number(key2);
                            cutsOf[key2] = cutsByMeasure(row.children[cellIndex], texts[key2], limitPx, probeRow);
                        }
                    }
                    probeRow.remove();
                    assembled = assemble();
                    parts = buildParts(row, assembled.parts, assembled.count, amountCell, amountText, null);
                }

                guard.splitRows++;
                guard.maxParts = Math.max(guard.maxParts, parts.length);
                if (parts.length > MAX_PARTS) {
                    throw new Error("a row splits into more parts than the guard admits");
                }

                var selfCheck = "";
                for (var t = 0; t < cells.length; t++) {
                    var cellPosition = Array.prototype.indexOf.call(row.children, cells[t]);
                    var joined = parts.map(function (part) {
                        var child = part.children[cellPosition];
                        return child ? child.textContent : "";
                    }).join("");
                    selfCheck += joined;
                    if (joined !== texts[String(cellPosition)]) {
                        throw new Error("the parts do not concatenate to the row's text");
                    }
                }
                replacements.push({ row: row, parts: parts });
            }

            for (var r = 0; r < replacements.length; r++) {
                var replacement = replacements[r];
                replacement.row.replaceWith.apply(replacement.row, replacement.parts);
            }
        } finally {
            box.remove();
        }
    }

    // ---- account after layout ----------------------------------------------------

    function pageElements() {
        return Array.prototype.slice.call(document.querySelectorAll(".pagedjs_pages > .pagedjs_page"));
    }

    function fragmentsOf(pageElement) {
        return Array.prototype.slice.call(pageElement.querySelectorAll(".pagedjs_page_content [data-seq]"));
    }

    function account(options) {
        // The print-level page is the sheet: Paged.js already owns the layout-time page box
        // (the template's margins are its content frame), so the physical page must be the
        // sheet's own size with no margin. The style is added after the layout, once Paged.js
        // has consumed the page rules it reads, and it never applies on screen.
        var printStyle = document.createElement("style");
        printStyle.setAttribute("data-accounting-print", "true");
        printStyle.appendChild(document.createTextNode(
            "@media print { @page { size: " + options.pageWidthMm + "mm " + options.pageHeightMm
            + "mm; margin: 0 !important; } }"));
        document.head.appendChild(printStyle);

        var pages = pageElements();
        guard.pages = pages.length;
        if (pages.length === 0) {
            throw new Error("no laid-out page");
        }

        var rows = {};
        var order = [];
        var perPage = pages.map(function (pageElement, pageIndex) {
            var fragments = fragmentsOf(pageElement);
            return fragments.map(function (element) {
                var seq = element.getAttribute("data-seq");
                if (!rows[seq]) {
                    rows[seq] = [];
                    order.push(seq);
                }
                rows[seq].push({ page: pageIndex, element: element, index: rows[seq].length });
                return { seq: seq, element: element };
            });
        });

        var opening = toUnits(document.documentElement.getAttribute("data-brought-forward") || "0", options.scale);
        var openedWith = document.documentElement.getAttribute("data-group-opened-with");
        var openGroup = document.documentElement.getAttribute("data-open-group");

        var brought = opening;
        pages.forEach(function (pageElement, pageIndex) {
            var subtotal = 0n;
            var fragments = perPage[pageIndex];
            for (var i = 0; i < fragments.length; i++) {
                var seq = fragments[i].seq;
                var fragmentsOfRow = rows[seq];
                var isLast = fragments[i].element === fragmentsOfRow[fragmentsOfRow.length - 1].element;
                var amountCell = fragments[i].element.querySelector('[data-role="amount"]');
                if (isLast) {
                    subtotal += toUnits(fragments[i].element.getAttribute("data-amount"), options.scale);
                } else if (amountCell) {
                    amountCell.textContent = MARK;
                }
            }

            var carried = brought + subtotal;
            writeBand(pageElement, options, [
                options.labels.broughtForward,
                options.labels.pageSubtotal,
                options.labels.carriedForward
            ], [
                format(brought, options),
                format(subtotal, options),
                format(carried, options)
            ]);
            brought = carried;
        });

        var lastPage = pages[pages.length - 1];
        var lastFragments = fragmentsOf(lastPage);
        for (var f = 0; f < lastFragments.length; f++) {
            if (lastFragments[f].hasAttribute("data-split-to")) {
                guard.unfinishedLastPage = true;
            }
        }

        // Every final group's total, from the group's own rows plus the opening the
        // service recorded for the group this block continues. A row is summed once, at its
        // last fragment: Paged.js may split one row over pages, and every fragment carries
        // the same attributes.
        var groupCells = Array.prototype.slice.call(document.querySelectorAll('[data-role="group-total"][data-group-final="true"]'));
        for (var g = 0; g < groupCells.length; g++) {
            var cell = groupCells[g];
            var key = cell.getAttribute("data-group");
            var total = 0n;
            for (var seqIndex = 0; seqIndex < order.length; seqIndex++) {
                var fragmentsOfSeq = rows[order[seqIndex]];
                var lastFragment = fragmentsOfSeq[fragmentsOfSeq.length - 1].element;
                if (lastFragment.getAttribute("data-group") === key) {
                    total += toUnits(lastFragment.getAttribute("data-amount"), options.scale);
                }
            }
            if (openGroup !== null && openedWith !== null && String(key) === String(openGroup)) {
                total += toUnits(openedWith, options.scale);
            }
            cell.textContent = format(total, options);
        }
    }

    function writeBand(pageElement, options, labels, values) {
        var band = document.createElement("div");
        band.className = "accounting-band";
        band.setAttribute("data-accounting-band", "true");

        // The band positions itself in its own inline declaration: the injected stylesheet
        // is passed through Paged.js's own stylesheet handling, and a rule that does not
        // survive it must never turn the band into content-area text.
        band.style.position = "absolute";
        band.style.overflow = "hidden";
        band.style.left = "0";
        band.style.top = "0";
        band.style.width = options.footer.widthMm + "mm";
        band.style.height = options.footer.heightMm + "mm";
        band.style.fontSize = (options.footer.heightMm * 0.2) + "mm";
        band.style.lineHeight = (options.footer.heightMm / 3) + "mm";
        for (var i = 0; i < labels.length; i++) {
            var line = document.createElement("div");
            line.textContent = labels[i] + " " + values[i];
            band.appendChild(line);
        }
        pageElement.appendChild(band);
        if (band.children.length !== 3) {
            throw new Error("the accounting band does not hold its three lines");
        }

        // The declared rectangle is in the page's own millimetres: the page element may be
        // laid out at a scale, so the millimetres are converted through its measured box and
        // the result is expressed in the coordinates of the band's containing block.
        var pageBox = pageElement.getBoundingClientRect();
        var host = band.offsetParent ? band.offsetParent.getBoundingClientRect() : pageBox;
        var perMmX = pageBox.width / options.pageWidthMm;
        var perMmY = pageBox.height / options.pageHeightMm;
        band.style.left = (pageBox.left - host.left + options.footer.leftMm * perMmX) + "px";
        band.style.top = (pageBox.top - host.top + options.footer.topMm * perMmY) + "px";
        band.style.width = (options.footer.widthMm * perMmX) + "px";
        band.style.height = (options.footer.heightMm * perMmY) + "px";
    }

    // ---- start ------------------------------------------------------------------

    window.__pageAccounting = async function () {
        var options = facts();
        try {
            if (options === null) {
                throw new Error("the accounting facts are absent");
            }
            guard.phase = "split";
            style();
            await document.fonts.ready;
            splitRows(options);
            guard.phase = "layout";
            await new Paged.Previewer().preview();
            guard.phase = "account";
            account(options);
            if (guard.partOverLimit > 0) {
                throw new Error("a part exceeds the part limit");
            }
            if (guard.unfinishedLastPage) {
                throw new Error("the last page holds an unfinished row");
            }
            guard.phase = "done";
            document.documentElement.dataset.layout = "done";
        } catch (e) {
            guard.phase = "failed";
            fail("layout-guard", e);
        }
    };

    // The script is served with Paged.js's auto-start switched off, so the service's
    // print waits for the done signal this call sets; it runs as soon as the document
    // is parsed, which is when this script executes.
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () { window.__pageAccounting(); });
    } else {
        window.__pageAccounting();
    }
})();
