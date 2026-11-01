// Group-behaviour script of the group-rules experiment.
//
// Loaded by the PDF page only, last (after the pagination library, the unchanged
// accounting script of spikes/02-pdf-layout and the pre-split script of
// spikes/08-pagedjs-pre-split), and only when a configuration attempts a group
// behaviour. Its data-rules attribute names the behaviours, any of:
//
// - "keep-header": a group header is never left alone at the end of a page. When
//   the content that does not fit a page starts at a group's first row, the page
//   ends before that group's header instead, so the header moves with its row.
// - "keep-total": a group total stays on the page of its group's last row. When
//   the content that does not fit starts at a group total, the page ends before
//   the group's last row instead, so the row moves with its total.
// - "repeat-header": each group's rows are wrapped, before the layout starts, in a
//   container that carries the header text followed by " (cont.)"; the stylesheet
//   prints that text at the top of every later fragment of the container that does
//   not hold the group's own header.
//
// A break is only ever moved earlier, by at most two rows (the last row, then the
// header before it), and never before the first row of a page, so no page is left
// empty and no content moves later. Text moves only by moving existing nodes; the
// one new text is an attribute value printed by the stylesheet as text.
// window.__groupRules holds the rules, the groups wrapped, the breaks moved and any
// error; once the layout is finished it is written to the console as one line
// starting "group-rules:" (a diagnostic; no check reads it).
(function () {
    "use strict";

    const script = document.currentScript;
    const rules = new Set(((script && script.dataset.rules) || "").split(/\s+/).filter(function (rule) {
        return rule.length > 0;
    }));
    const state = { rules: Array.from(rules), groups: 0, moved: { keepHeader: 0, keepTotal: 0 }, error: null };
    window.__groupRules = state;

    const ROW = ".item, .group-start, .group-end, .block-total";
    const PAGE_ROWS = ".title, .free, .grid-head, .group-start, .item, .group-end, .block-total";
    const movedOnce = new Set();

    // The node a range starts at: the child it points at, or its text node.
    function startNode(range) {
        const container = range.startContainer;
        if (container.nodeType === Node.ELEMENT_NODE && range.startOffset < container.childNodes.length) {
            return container.childNodes[range.startOffset];
        }
        return container;
    }

    // The row element that holds the node, or is the node; null when there is none inside the page.
    function rowOf(node, rendered) {
        let element = node && node.nodeType === Node.ELEMENT_NODE ? node : node ? node.parentElement : null;
        while (element && element !== rendered) {
            if (element.matches(ROW)) {
                return element;
            }
            element = element.parentElement;
        }
        return null;
    }

    // True when nothing printed precedes the element on its page.
    function firstRowOfPage(element, rendered) {
        return rendered.querySelector(PAGE_ROWS) === element;
    }

    function count(rule, element) {
        const key = rule + ":" + (element.getAttribute("data-ref") || "");
        if (!movedOnce.has(key)) {
            movedOnce.add(key);
            state.moved[rule === "keep-header" ? "keepHeader" : "keepTotal"]++;
        }
    }

    class GroupBreakHandler extends Paged.Handler {
        constructor(chunker, polisher, caller) {
            super(chunker, polisher, caller);
        }

        onOverflow(overflow, rendered) {
            if (!overflow || !rendered) {
                return undefined;
            }
            const row = rowOf(startNode(overflow), rendered);
            if (!row) {
                return undefined;
            }
            let start = row;
            if (rules.has("keep-total") && row.matches(".group-end")) {
                const last = row.previousElementSibling;
                if (last && last.matches(".item") && !firstRowOfPage(last, rendered)) {
                    start = last;
                    count("keep-total", last);
                }
            }
            if (rules.has("keep-header") && start.matches(".item")) {
                const header = start.previousElementSibling;
                if (header && header.matches(".group-start") && !firstRowOfPage(header, rendered)) {
                    start = header;
                    count("keep-header", header);
                }
            }
            if (start === row) {
                return undefined;
            }
            const range = document.createRange();
            range.setStartBefore(start);
            range.setEndAfter(rendered.lastChild);
            return range;
        }
    }

    class GroupDiagnosticsHandler extends Paged.Handler {
        constructor(chunker, polisher, caller) {
            super(chunker, polisher, caller);
        }

        afterRendered() {
            let hasRule = false;
            for (const sheet of Array.from(document.styleSheets)) {
                let list = null;
                try {
                    list = sheet.cssRules;
                } catch (e) {
                    continue;
                }
                for (const rule of Array.from(list)) {
                    if (rule.selectorText && rule.selectorText.indexOf(":has(") >= 0) {
                        hasRule = true;
                    }
                }
            }
            console.info("group-rules: " + JSON.stringify(state) + " hasRule=" + hasRule);
        }
    }

    // Wraps each group's rows in a container: the header, the rows after it, up to and
    // including its total, or up to the next header or the block total for an open group.
    function wrapGroups() {
        const grid = document.querySelector(".rows-grid");
        if (!grid) {
            throw new Error("No grid block in the page.");
        }
        const children = Array.from(grid.children);
        let i = 0;
        while (i < children.length) {
            const header = children[i];
            if (!header.classList.contains("group-start")) {
                i++;
                continue;
            }
            const group = document.createElement("div");
            group.className = "group";
            group.setAttribute("data-cont", header.textContent.trim() + " (cont.)");
            header.before(group);
            let j = i;
            while (j < children.length) {
                const child = children[j];
                if (j > i && (child.classList.contains("group-start") || child.classList.contains("block-total"))) {
                    break;
                }
                group.appendChild(child);
                j++;
                if (child.classList.contains("group-end")) {
                    break;
                }
            }
            state.groups++;
            i = j;
        }
    }

    if (rules.has("keep-header") || rules.has("keep-total")) {
        Paged.registerHandlers(GroupBreakHandler);
    }
    if (rules.size > 0) {
        Paged.registerHandlers(GroupDiagnosticsHandler);
    }

    if (rules.has("repeat-header")) {
        const startLayout = window.__startLayout;
        window.__startLayout = function () {
            try {
                wrapGroups();
            } catch (e) {
                state.error = String(e && e.stack ? e.stack : e);
                console.info("group-rules: " + JSON.stringify(state));
                document.documentElement.dataset.layout = "failed";
                return Promise.resolve();
            }
            return startLayout();
        };
    }
})();
