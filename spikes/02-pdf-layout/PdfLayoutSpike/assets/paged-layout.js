// In-page layout script of the layout experiment.
//
// - A pagination handler records, while each page is laid out, which rows it
//   holds (diagnostic only).
// - Once the whole block is laid out, it computes the page subtotals over the
//   finished layout: a row that spans pages is counted once, on the page where
//   it ends; earlier fragments get a "(continues)" mark in the amount column.
//   Money is handled as integer cents (BigInt), never as binary floating point.
// - The footer (brought forward, page subtotal, carried forward) is written into
//   the bottom margin boxes, which exist before layout, so it cannot move rows.
// - document.documentElement.dataset.layout = "done" | "failed" is the explicit
//   layout-finished signal the harness waits for.
(function () {
    "use strict";

    const result = {
        total: null,
        flowMs: null,
        footerOn: null,
        marginBoxesFound: null,
        pages: [],
        diagnostics: [],
        scripts: 0,
        accountingError: null,
        error: null
    };
    window.__layoutResult = result;

    function toCents(text) {
        const match = /^(-?)(\d+)\.(\d{2})$/.exec(String(text).trim());
        if (!match) {
            throw new Error("Unexpected amount text: " + text);
        }
        const value = BigInt(match[2]) * 100n + BigInt(match[3]);
        return match[1] === "-" ? -value : value;
    }

    function formatCents(value) {
        const negative = value < 0n;
        const abs = negative ? -value : value;
        const text = (abs / 100n).toString() + "." + (abs % 100n).toString().padStart(2, "0");
        return (negative ? "-" : "") + text;
    }

    function itemFragments(pageElement) {
        return Array.from(pageElement.querySelectorAll(".pagedjs_page_content [data-seq]"));
    }

    function amountCell(fragment) {
        return fragment.querySelector(":scope > .amount");
    }

    function descText(fragment) {
        const desc = fragment.querySelector(":scope > .desc");
        return desc ? desc.textContent : "";
    }

    function writeMark(fragment, cell) {
        let target = cell;
        if (!target) {
            target = document.createElement(fragment.tagName === "TR" ? "td" : "div");
            target.className = "amount";
            fragment.appendChild(target);
        }
        target.textContent = "";
        const mark = document.createElement("span");
        mark.className = "continues";
        mark.textContent = "(continues)";
        target.appendChild(mark);
    }

    function footerBox(pageElement, position) {
        const box = pageElement.querySelector(".pagedjs_margin-bottom-" + position);
        const content = box ? box.querySelector(".pagedjs_margin-content") : null;
        return { box: box, content: content };
    }

    function overflows(box, content) {
        if (!box || !content || !content.firstChild) {
            return false;
        }
        const outer = box.getBoundingClientRect();
        const range = document.createRange();
        range.selectNodeContents(content);
        const inner = range.getBoundingClientRect();
        return inner.left < outer.left - 0.5 || inner.right > outer.right + 0.5 ||
            inner.top < outer.top - 0.5 || inner.bottom > outer.bottom + 0.5 ||
            content.scrollWidth > content.clientWidth + 1;
    }

    function account() {
        const footerOn = document.body.dataset.footer !== "off";
        result.footerOn = footerOn;
        const pageElements = Array.from(document.querySelectorAll(".pagedjs_pages > .pagedjs_page"));

        // Pass 1: where every row appears.
        const pagesOfSeq = new Map();
        const perPage = pageElements.map(function (pageElement, pageIndex) {
            return itemFragments(pageElement).map(function (element) {
                const seq = element.dataset.seq;
                const list = pagesOfSeq.get(seq) || [];
                list.push(pageIndex);
                pagesOfSeq.set(seq, list);
                return { element: element, seq: seq, index: list.length - 1 };
            });
        });

        // Reference column positions: the first row that is not split.
        let reference = null;
        for (const fragments of perPage) {
            for (const f of fragments) {
                if (pagesOfSeq.get(f.seq).length === 1) {
                    const desc = f.element.querySelector(":scope > .desc");
                    const amount = amountCell(f.element);
                    if (desc && amount) {
                        reference = { desc: desc.getBoundingClientRect().left, amount: amount.getBoundingClientRect().left };
                        break;
                    }
                }
            }
            if (reference) {
                break;
            }
        }

        let marginBoxesFound = true;
        let brought = 0n;
        pageElements.forEach(function (pageElement, pageIndex) {
            let subtotal = 0n;
            const fragments = [];
            for (const f of perPage[pageIndex]) {
                const count = pagesOfSeq.get(f.seq).length;
                const isLast = f.index === count - 1;
                const cell = amountCell(f.element);
                const amountBefore = cell ? cell.textContent.trim() : "";
                if (isLast) {
                    subtotal += toCents(f.element.dataset.amount);
                } else if (footerOn) {
                    writeMark(f.element, cell);
                }
                fragments.push({
                    seq: Number(f.seq),
                    fragment: f.index,
                    fragments: count,
                    splitFrom: f.element.hasAttribute("data-split-from"),
                    splitTo: f.element.hasAttribute("data-split-to"),
                    amountBefore: amountBefore,
                    element: f.element
                });
            }

            const carried = brought + subtotal;
            const texts = {
                left: "Brought forward: " + formatCents(brought),
                center: "Page subtotal: " + formatCents(subtotal),
                right: "Carried forward: " + formatCents(carried)
            };
            const boxes = { left: footerBox(pageElement, "left"), center: footerBox(pageElement, "center"), right: footerBox(pageElement, "right") };
            if (footerOn) {
                for (const position of ["left", "center", "right"]) {
                    if (boxes[position].content) {
                        boxes[position].content.textContent = texts[position];
                    } else {
                        marginBoxesFound = false;
                    }
                }
            }

            // Read back what the page now shows.
            const area = pageElement.querySelector(".pagedjs_area");
            const areaRect = area ? area.getBoundingClientRect() : null;
            const areaBottom = areaRect ? areaRect.bottom : Number.NaN;
            const belowArea = [];
            const out = fragments.map(function (f) {
                const rect = f.element.getBoundingClientRect();
                if (!(rect.bottom <= areaBottom + 0.5)) {
                    belowArea.push(f.seq);
                }
                let aligned = null;
                if (f.fragments > 1 && reference) {
                    const desc = f.element.querySelector(":scope > .desc");
                    const amount = amountCell(f.element);
                    aligned = !!desc && !!amount &&
                        Math.abs(desc.getBoundingClientRect().left - reference.desc) <= 1 &&
                        Math.abs(amount.getBoundingClientRect().left - reference.amount) <= 1;
                }
                return {
                    seq: f.seq,
                    fragment: f.fragment,
                    fragments: f.fragments,
                    splitFrom: f.splitFrom,
                    splitTo: f.splitTo,
                    amountBefore: f.amountBefore,
                    mark: f.element.querySelector(":scope > .amount .continues") !== null,
                    aligned: aligned,
                    descChars: descText(f.element).length,
                    top: rect.top,
                    bottom: rect.bottom
                };
            });
            const footerOverflow = [];
            const footer = {};
            for (const position of ["left", "center", "right"]) {
                const b = boxes[position];
                footer[position] = b.content ? b.content.textContent : null;
                if (footerOn && overflows(b.box, b.content)) {
                    footerOverflow.push(position);
                }
            }
            result.pages.push({
                page: pageIndex + 1,
                fragments: out,
                footer: footer,
                marks: pageElement.querySelectorAll(".continues").length,
                areaBottom: areaBottom,
                areaTop: areaRect ? areaRect.top : null,
                areaHeight: areaRect ? areaRect.height : null,
                belowArea: belowArea,
                footerOverflow: footerOverflow
            });
            brought = carried;
        });
        result.marginBoxesFound = marginBoxesFound;
    }

    class LayoutHandler extends Paged.Handler {
        constructor(chunker, polisher, caller) {
            super(chunker, polisher, caller);
        }

        afterPageLayout(pageElement, page, breakToken) {
            const fragments = itemFragments(pageElement);
            const pages = pageElement.parentNode;
            const index = Array.prototype.indexOf.call(pages.children, pageElement);
            let previousLastSplitToKnown = null;
            if (index > 0) {
                const previous = itemFragments(pages.children[index - 1]);
                const last = previous[previous.length - 1];
                previousLastSplitToKnown = last ? last.hasAttribute("data-split-to") : null;
            }
            const last = fragments[fragments.length - 1];
            result.diagnostics.push({
                page: index + 1,
                fragmentCount: fragments.length,
                firstSplitFrom: fragments.length > 0 && fragments[0].hasAttribute("data-split-from"),
                lastSplitToAtLayout: !!last && last.hasAttribute("data-split-to"),
                hasBreakToken: !!breakToken,
                previousLastSplitToKnown: previousLastSplitToKnown
            });
        }

        afterRendered() {
            try {
                account();
            } catch (e) {
                result.accountingError = String(e && e.stack ? e.stack : e);
            }
        }
    }

    Paged.registerHandlers(LayoutHandler);

    window.__startLayout = async function () {
        const root = document.documentElement;
        try {
            await document.fonts.ready;
            const flow = await window.PagedPolyfill.preview();
            if (result.accountingError) {
                throw new Error(result.accountingError);
            }
            result.total = flow.total;
            result.flowMs = flow.performance;
            result.scripts = document.querySelectorAll("script").length;
            root.dataset.layout = "done";
        } catch (e) {
            result.error = String(e && e.stack ? e.stack : e);
            root.dataset.layout = "failed";
        }
    };
})();
