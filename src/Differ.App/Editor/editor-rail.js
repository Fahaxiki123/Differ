"use strict";

const differenceRail = document.getElementById("difference-rail");
const railViewport = document.getElementById("rail-viewport");
const railCanvas = document.getElementById("rail-markers");
const railCurrent = document.getElementById("rail-current");
const railPreview = document.getElementById("rail-preview");
const railMinimumMarkHeight = 3;
const railHitDistance = 4;
let railMarks = [], railOccupied = [], railFrame = 0, railMarkersDirty = true, railColorsDirty = false;
let railDrag = null, railPreviewIndex = -1;

function scheduleDifferenceRail(markersChanged = false) {
    railMarkersDirty ||= markersChanged;
    if (!railFrame) {
        railFrame = requestAnimationFrame(updateDifferenceRail);
    }
}

function scheduleDifferenceRailRepaint() {
    railColorsDirty = true;
    scheduleDifferenceRail();
}

function differenceRailGeometry() {
    const layout = right.getLayoutInfo();
    // Monaco includes the visible horizontal scrollbar in both its scroll
    // extent and viewport. Remove that shared strip from the rail geometry.
    const horizontal = right.getScrollWidth() > layout.contentWidth ? layout.horizontalScrollbarHeight : 0;
    const height = Math.max(0, layout.height - horizontal);
    const extent = Math.max(height, right.getScrollHeight() - horizontal);
    const maximum = extent - height;
    const scroll = Math.max(0, Math.min(maximum, right.getScrollTop()));
    return { height, extent, maximum, scroll, scale: extent ? height / extent : 0 };
}

function updateDifferenceRail() {
    railFrame = 0;
    if (!right) return;
    const geometry = differenceRailGeometry();
    differenceRail.style.height = `${geometry.height}px`;
    differenceRail.setAttribute("aria-valuemax", String(Math.round(geometry.maximum)));
    differenceRail.setAttribute("aria-valuenow", String(Math.round(geometry.scroll)));
    railViewport.style.top = `${geometry.scroll * geometry.scale}px`;
    railViewport.style.height = `${geometry.height * geometry.scale}px`;
    if (railMarkersDirty || railColorsDirty) {
        if (railMarkersDirty) rebuildDifferenceRailMarkers(geometry);
        railMarkersDirty = false;
        railColorsDirty = false;
        paintDifferenceRailMarkers(geometry);
    }
    const current = differenceRailMark(activeHunk, geometry);
    railCurrent.hidden = !current;
    if (current) {
        const size = Math.min(8, current.bottom - current.top);
        railCurrent.style.top = `${current.y - size / 2}px`;
        railCurrent.style.height = `${size}px`;
    }
}

function differenceRailMark(index, geometry) {
    if (!geometry.height || !geometry.scale || index < 0 || index >= lineChanges.length
        || !modified || !projectionVersion || projectionVersion !== version || hasPendingText) {
        return null;
    }
    const ranges = [left, right].map((editor, side) => {
        const range = sideRange(lineChanges[index], side);
        if (!range.end) return null;
        const start = editor.getTopForLineNumber(range.start) * geometry.scale;
        const end = editor.getBottomForLineNumber(range.end) * geometry.scale;
        const size = Math.min(geometry.height, Math.max(railMinimumMarkHeight, end - start));
        const top = Math.max(0, Math.min(geometry.height - size, (start + end - size) / 2));
        return { top, bottom: top + size };
    });
    const present = ranges.filter(Boolean);
    if (!present.length) return null;
    const top = Math.min(...present.map(range => range.top));
    const bottom = Math.max(...present.map(range => range.bottom));
    return { index, ranges, top, bottom, y: (top + bottom) / 2 };
}

function rebuildDifferenceRailMarkers(geometry) {
    railMarks = [];
    railOccupied = [];
    if (!railCanvas.clientWidth || !geometry.height || !lineChanges.length || !projectionVersion || projectionVersion !== version || hasPendingText) {
        hideRailPreview();
        return;
    }
    const buckets = new Map();
    // A frame can paint at most two marked runs per track pixel, independent
    // of document length. A palette change can reuse these occupied pixels.
    const pixels = Math.ceil(geometry.height);
    railOccupied = [new Uint8Array(pixels), new Uint8Array(pixels)];
    for (let index = 0; index < lineChanges.length; index++) {
        const mark = differenceRailMark(index, geometry);
        if (!mark) continue;
        const pixel = Math.round(mark.y);
        if (!buckets.has(pixel) || index === activeHunk) buckets.set(pixel, mark);
        mark.ranges.forEach((range, side) => {
            if (range) railOccupied[side].fill(1, Math.round(range.top), Math.max(Math.round(range.top) + 1, Math.round(range.bottom)));
        });
    }
    railMarks = [...buckets.values()];
    if (!railMarks.some(mark => mark.index === railPreviewIndex)) hideRailPreview();
}

function paintDifferenceRailMarkers(geometry) {
    const density = window.devicePixelRatio || 1;
    const width = railCanvas.clientWidth;
    railCanvas.width = Math.round(width * density);
    railCanvas.height = Math.round(geometry.height * density);
    if (!width || !geometry.height || !railOccupied.length) return;
    const context = railCanvas.getContext("2d");
    if (!context) return;
    context.setTransform(density, 0, 0, density, 0, 0);
    const palette = getComputedStyle(differenceRail);
    const colors = [
        palette.getPropertyValue("--railDeleted").trim() || palette.getPropertyValue("--deletedMarker").trim(),
        palette.getPropertyValue("--railAdded").trim() || palette.getPropertyValue("--addedMarker").trim()
    ];
    railOccupied.forEach((pixels, side) => {
        context.fillStyle = colors[side];
        const x = Math.round((side + .5) * width / 2 - 2);
        for (let start = 0; start < pixels.length; start++) {
            if (!pixels[start]) continue;
            let end = start + 1;
            while (pixels[end]) end++;
            context.fillRect(x, start, 4, end - start);
            start = end - 1;
        }
    });
}

function railMarkerAt(y) {
    let closest = null, distance = Infinity;
    for (const mark of railMarks) {
        if (y < mark.top - railHitDistance || y > mark.bottom + railHitDistance) continue;
        const candidateDistance = Math.abs(y - mark.y);
        if (candidateDistance < distance) {
            distance = candidateDistance;
            closest = mark;
        }
    }
    return closest;
}

function setRailScroll(top) {
    right.setScrollTop(top, monaco.editor.ScrollType.Immediate);
    scheduleDifferenceRail();
}

function hideRailPreview() {
    railPreview.hidden = true;
    railPreviewIndex = -1;
}

function previewText(className, value) {
    const element = document.createElement("div");
    element.className = className;
    element.textContent = value;
    return element;
}

function showRailPreview(mark) {
    if (mark.index !== railPreviewIndex) {
        const change = lineChanges[mark.index];
        const ranges = [sideRange(change, 0), sideRange(change, 1)];
        const counts = ranges.map(range => range.end ? range.end - range.start + 1 : 0);
        const changedLines = Math.max(...counts);
        const title = previewText("rail-preview-title", formatLabel(navigationLabels.overviewPreview || "差异 {0} / {1}", mark.index + 1, lineChanges.length));
        const columns = previewText("rail-preview-columns", "");
        const labels = [navigationLabels.overviewBase || "BASE", navigationLabels.overviewMine || "工作副本"];
        [original, modified].forEach((model, side) => {
            const column = previewText("rail-preview-side", "");
            column.append(previewText("rail-preview-heading", labels[side]));
            const count = counts[side];
            const start = count ? ranges[side].start : ranges[side].start + 1;
            function addLine(line, changed) {
                const valid = line > 0 && line <= model.getLineCount();
                const row = previewText(`rail-preview-line${changed && valid ? (side === 0 ? " old" : " new") : ""}`, "");
                const number = document.createElement("span");
                number.className = "rail-preview-number";
                number.textContent = valid ? `${line}  ` : "   ";
                row.append(number, valid ? model.getLineContent(line).slice(0, 400) : " ");
                column.append(row);
            }
            for (let offset = -3; offset < 0; offset++) addLine(start + offset, false);
            for (let offset = 0; offset < Math.min(6, changedLines); offset++) {
                addLine(offset < count ? start + offset : 0, true);
            }
            if (changedLines > 6) column.append(previewText("rail-preview-line", "…"));
            for (let offset = 0; offset < 3; offset++) addLine(start + count + offset, false);
            columns.append(column);
        });
        const hint = previewText("rail-preview-hint", navigationLabels.overviewHint || "单击差异定位，拖动视窗带滚动");
        railPreview.replaceChildren(title, columns, hint);
        railPreviewIndex = mark.index;
    }
    railPreview.hidden = false;
    const rail = differenceRail.getBoundingClientRect();
    railPreview.style.left = `${Math.max(4, rail.left - railPreview.offsetWidth - 8)}px`;
    railPreview.style.top = `${Math.max(4, Math.min(innerHeight - railPreview.offsetHeight - 4, rail.top + mark.y - railPreview.offsetHeight / 2))}px`;
}

function initializeDifferenceRail() {
    const railY = event => event.clientY - differenceRail.getBoundingClientRect().top;
    differenceRail.addEventListener("pointerdown", event => {
        if (event.button !== 0 || railDrag || !modified) return;
        event.preventDefault();
        hideRailPreview();
        const y = railY(event);
        const geometry = differenceRailGeometry();
        if (!geometry.scale) return;
        const relative = y - geometry.scroll * geometry.scale;
        const bandHeight = geometry.height * geometry.scale;
        const inBand = relative >= 0 && relative <= bandHeight;
        railDrag = {
            id: event.pointerId, y, moved: false, inBand,
            marker: railMarkerAt(y), anchor: inBand ? relative : bandHeight / 2
        };
        if (event.isTrusted) differenceRail.setPointerCapture(event.pointerId);
    });
    differenceRail.addEventListener("pointermove", event => {
        const y = railY(event);
        if (railDrag?.id === event.pointerId) {
            if (railDrag.moved || Math.abs(y - railDrag.y) >= 3) {
                railDrag.moved = true;
                differenceRail.classList.add("is-dragging");
                const { scale } = differenceRailGeometry();
                if (scale) setRailScroll((y - railDrag.anchor) / scale);
            }
            return;
        }
        const mark = railMarkerAt(y);
        if (mark && modified && projectionVersion && projectionVersion === version && !hasPendingText) showRailPreview(mark);
        else hideRailPreview();
    });
    function endDrag(event, activate) {
        if (railDrag?.id !== event.pointerId) return;
        const drag = railDrag;
        railDrag = null;
        differenceRail.classList.remove("is-dragging");
        if (!activate || drag.moved || !modified) return;
        if (drag.marker) selectHunk(drag.marker.index, true);
        else if (!drag.inBand) {
            const { scale } = differenceRailGeometry();
            if (scale) setRailScroll((railY(event) - drag.anchor) / scale);
        }
    }
    differenceRail.addEventListener("pointerup", event => endDrag(event, true));
    differenceRail.addEventListener("pointercancel", event => endDrag(event, false));
    differenceRail.addEventListener("lostpointercapture", event => endDrag(event, false));
    differenceRail.addEventListener("pointerleave", hideRailPreview);
    differenceRail.addEventListener("blur", hideRailPreview);
    differenceRail.addEventListener("wheel", event => {
        if (!modified || event.ctrlKey || event.metaKey) return;
        event.preventDefault();
        const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? right.getOption(monaco.editor.EditorOption.lineHeight)
            : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? differenceRailGeometry().height : 1;
        setRailScroll(right.getScrollTop() + event.deltaY * unit);
    }, { passive: false });
    differenceRail.addEventListener("keydown", event => {
        if (!modified || event.altKey) return;
        if (event.ctrlKey || event.metaKey) {
            const key = event.key.toLowerCase();
            if (key === "s") {
                flushText();
                send({ type: event.shiftKey ? "saveAs" : "save", session, version });
            } else if (key === "w") {
                send({ type: "close" });
            } else if (editMode && (key === "z" || key === "y")) {
                right.trigger("differ", key === "y" || event.shiftKey ? "redo" : "undo", null);
            } else {
                return;
            }
            event.preventDefault();
            return;
        }
        const { height, maximum, scroll } = differenceRailGeometry();
        const step = right.getOption(monaco.editor.EditorOption.lineHeight);
        switch (event.key) {
        case "ArrowUp": setRailScroll(scroll - step); break;
        case "ArrowDown": setRailScroll(scroll + step); break;
        case "PageUp": setRailScroll(scroll - height); break;
        case "PageDown": setRailScroll(scroll + height); break;
        case "Home": setRailScroll(0); break;
        case "End": setRailScroll(maximum); break;
        case "F7": send({ type: "navigate", direction: -1 }); break;
        case "F8": send({ type: "navigate", direction: 1 }); break;
        case "Escape": hideRailPreview(); break;
        default: return;
        }
        event.preventDefault();
    });
}

function resetDifferenceRail() {
    if (railDrag && differenceRail.hasPointerCapture(railDrag.id)) {
        differenceRail.releasePointerCapture(railDrag.id);
    }
    railDrag = null;
    differenceRail.classList.remove("is-dragging");
    hideRailPreview();
    scheduleDifferenceRail(true);
}
