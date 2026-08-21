(() => {
    let returnFocus = null;
    document.addEventListener("click", () => {
        if (document.activeElement instanceof HTMLElement) returnFocus = document.activeElement;
    }, true);
    document.addEventListener("keydown", event => {
        if ((event.key === "Enter" || event.key === " ") && document.activeElement instanceof HTMLElement) returnFocus = document.activeElement;
    }, true);
    new MutationObserver(mutations => {
        let dialogAdded = false;
        let dialogRemoved = false;
        for (const mutation of mutations) {
            dialogAdded ||= [...mutation.addedNodes].some(node => node instanceof Element && (node.matches('[role="dialog"]') || node.querySelector('[role="dialog"]')));
            dialogRemoved ||= [...mutation.removedNodes].some(node => node instanceof Element && (node.matches('[role="dialog"]') || node.querySelector('[role="dialog"]')));
        }
        if (dialogAdded) {
            const dialog = document.querySelector('[role="dialog"]');
            const target = dialog?.querySelector('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href]');
            if (target instanceof HTMLElement) target.focus();
        } else if (dialogRemoved && returnFocus?.isConnected) {
            returnFocus.focus();
        }
    }).observe(document.body, { childList: true, subtree: true });
})();

window.slopFactoryMask = (() => {
    // Bounds undo/redo memory: entries are compressed PNG data URLs (a sparse mask — mostly
    // transparent with a few solid regions — compresses to a few KB regardless of canvas
    // resolution), not raw getImageData buffers, so a capped stack stays small even for a large
    // source image where a handful of full-resolution ImageData snapshots would not.
    const MaxHistoryEntries = 20;

    const getOrCreateOverlay = canvas => {
        const parent = canvas.parentElement;
        let overlay = parent.querySelector(':scope > .mask-cursor-overlay');
        if (!overlay) {
            overlay = document.createElement('div');
            overlay.className = 'mask-cursor-overlay';
            overlay.setAttribute('aria-hidden', 'true');
            parent.appendChild(overlay);
        }
        return overlay;
    };

    const point = (canvas, clientX, clientY) => {
        const r = canvas.getBoundingClientRect();
        return [(clientX - r.left) * canvas.width / r.width, (clientY - r.top) * canvas.height / r.height];
    };

    const cssRadius = canvas => {
        const r = canvas.getBoundingClientRect();
        return canvas._maskBrush.brushSize * (r.width / canvas.width);
    };

    const positionOverlayAt = (canvas, overlay, x, y, kind) => {
        const r = canvas.getBoundingClientRect();
        // offsetLeft/offsetTop (relative to .mask-canvas-viewport, the nearest positioned
        // ancestor) rather than a getBoundingClientRect() diff: the overlay is an absolutely
        // positioned sibling *inside* that same scrolling container, so its left/top must be
        // expressed in the container's unscrolled content coordinate space — a viewport-rect diff
        // would already have the container's own scroll subtracted out once by the browser and
        // once by us, drifting the cursor out of place as soon as a zoomed-in canvas is panned.
        const cssX = canvas.offsetLeft + x * (r.width / canvas.width);
        const cssY = canvas.offsetTop + y * (r.height / canvas.height);
        const diameter = cssRadius(canvas) * 2;
        overlay.style.width = `${diameter}px`;
        overlay.style.height = `${diameter}px`;
        overlay.style.left = `${cssX - diameter / 2}px`;
        overlay.style.top = `${cssY - diameter / 2}px`;
        overlay.style.display = 'block';
        overlay.classList.toggle('keyboard', kind === 'keyboard');
        overlay.classList.toggle('erasing', canvas._maskBrush.erasing === true);
    };

    const paintDab = (canvas, x, y) => {
        const context = canvas.getContext('2d');
        const b = canvas._maskBrush;
        context.globalCompositeOperation = b.erasing ? 'destination-out' : 'source-over';
        context.fillStyle = 'rgba(0,0,0,255)';
        context.beginPath();
        context.arc(x, y, b.brushSize, 0, Math.PI * 2);
        context.fill();
    };

    const pushUndoSnapshot = canvas => {
        canvas._undo.push(canvas.toDataURL('image/png'));
        if (canvas._undo.length > MaxHistoryEntries) canvas._undo.shift();
        canvas._redo = [];
    };

    const restoreSnapshot = async (canvas, dataUrl) => {
        const context = canvas.getContext('2d');
        context.clearRect(0, 0, canvas.width, canvas.height);
        if (!dataUrl) return;
        const response = await fetch(dataUrl);
        const bitmap = await createImageBitmap(await response.blob());
        context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        bitmap.close();
    };

    const announcePosition = (canvas, x, y) => {
        const label = canvas._maskBaseLabel ?? canvas.getAttribute('aria-label') ?? '';
        canvas._maskBaseLabel ??= label;
        const percentX = Math.round(x / canvas.width * 100);
        const percentY = Math.round(y / canvas.height * 100);
        canvas.setAttribute('aria-label', `${canvas._maskBaseLabel} (${percentX}%, ${percentY}%)`);
    };

    return {
        // brushSize/erasing are the current tool; sourceWidth/sourceHeight are the natural pixel
        // dimensions used to size the keyboard-cursor step relative to the image, independent of
        // whatever zoom level is applied afterward.
        initialize(canvas, imageUrl, brushSize = 32, erasing = false) {
            const context = canvas.getContext('2d');
            context.clearRect(0, 0, canvas.width, canvas.height);
            // CSS provides the source-image backdrop; the canvas's PNG pixels remain mask-only.
            canvas.style.backgroundImage = `url("${imageUrl}")`;
            canvas.style.backgroundSize = '100% 100%';
            canvas.style.backgroundPosition = 'center';
            canvas.style.backgroundRepeat = 'no-repeat';
            canvas.style.maxWidth = '100%';
            canvas.style.height = 'auto';
            canvas._maskBrush = { brushSize, erasing };
            canvas._undo = [];
            canvas._redo = [];
            canvas._kbX = canvas.width / 2;
            canvas._kbY = canvas.height / 2;

            const overlay = getOrCreateOverlay(canvas);
            let drawing = false;
            canvas.onpointerdown = e => {
                drawing = true;
                // Best-effort: a capture failure (observed for some synthetic/replayed pointer
                // sessions) must not abort the stroke itself — losing capture only means a fast
                // drag that leaves the canvas bounds stops updating until the pointer re-enters,
                // which is a much smaller loss than dropping the paint and undo entry entirely.
                try { canvas.setPointerCapture(e.pointerId); } catch { /* ignored, see above */ }
                pushUndoSnapshot(canvas);
                const [x, y] = point(canvas, e.clientX, e.clientY);
                paintDab(canvas, x, y);
                positionOverlayAt(canvas, overlay, x, y, 'pointer');
            };
            canvas.onpointermove = e => {
                const [x, y] = point(canvas, e.clientX, e.clientY);
                if (drawing) paintDab(canvas, x, y);
                positionOverlayAt(canvas, overlay, x, y, 'pointer');
            };
            canvas.onpointerup = () => { drawing = false; };
            canvas.onpointerleave = () => { if (!drawing) overlay.style.display = 'none'; };
            canvas.onpointercancel = () => { drawing = false; };

            // Keyboard-operable alternative to pointer painting (see paintAtKeyboardCursor's own
            // remarks) — handled natively here, not via a Blazor @onkeydown round-trip, so arrow
            // keys/space can be preventDefault-ed individually without also swallowing Tab and
            // breaking keyboard focus navigation out of the canvas.
            canvas.onkeydown = e => {
                const arrow = { ArrowUp: [0, -1], ArrowDown: [0, 1], ArrowLeft: [-1, 0], ArrowRight: [1, 0] }[e.key];
                if (arrow) {
                    e.preventDefault();
                    this.moveKeyboardCursor(canvas, arrow[0], arrow[1], e.shiftKey);
                } else if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    this.paintAtKeyboardCursor(canvas);
                    positionOverlayAt(canvas, overlay, canvas._kbX, canvas._kbY, 'keyboard');
                }
            };
            canvas.onfocus = () => positionOverlayAt(canvas, overlay, canvas._kbX, canvas._kbY, 'keyboard');
            canvas.onblur = () => { if (!drawing) overlay.style.display = 'none'; };
        },

        setBrush(canvas, brushSize, erasing) {
            canvas._maskBrush = { brushSize, erasing };
        },

        // percent is relative to the image's natural pixel size (100 = one canvas pixel per CSS
        // pixel). Wrap the canvas in a scrollable container (see app.css) so a zoomed-in large
        // image can still be panned by scrolling — the existing pointer-mapping math already
        // accounts for any CSS size via getBoundingClientRect, so zoom needs no separate handling.
        setZoom(canvas, percent) {
            canvas.style.maxWidth = 'none';
            canvas.style.width = `${canvas.width * percent / 100}px`;
            canvas.style.height = `${canvas.height * percent / 100}px`;
        },

        // Toggling canvas.style.opacity is a display-only compositing effect — it never touches
        // the actual pixel data toPng()/hasPixels() read, so it's safe to flip freely while editing.
        setPreviewOpacity(canvas, previewing) {
            canvas.style.opacity = previewing ? '0.45' : '1';
        },

        async clear(canvas) {
            pushUndoSnapshot(canvas);
            await restoreSnapshot(canvas, null);
        },

        canUndo(canvas) { return canvas._undo?.length > 0; },
        canRedo(canvas) { return canvas._redo?.length > 0; },

        async undo(canvas) {
            if (!(canvas._undo?.length > 0)) return;
            canvas._redo.push(canvas.toDataURL('image/png'));
            if (canvas._redo.length > MaxHistoryEntries) canvas._redo.shift();
            await restoreSnapshot(canvas, canvas._undo.pop());
        },

        async redo(canvas) {
            if (!(canvas._redo?.length > 0)) return;
            canvas._undo.push(canvas.toDataURL('image/png'));
            if (canvas._undo.length > MaxHistoryEntries) canvas._undo.shift();
            await restoreSnapshot(canvas, canvas._redo.pop());
        },

        // A keyboard-operable alternative to freehand pointer painting: arrow keys move a visible
        // dashed cursor ring by a step sized to the brush (Shift moves faster), Enter/Space paints
        // or erases a single dab at that position. Not equivalent to fluid freehand drawing, but a
        // real, discoverable way to build a mask without a pointer device.
        moveKeyboardCursor(canvas, dx, dy, fast) {
            const overlay = getOrCreateOverlay(canvas);
            const step = Math.max(4, canvas._maskBrush.brushSize / 2) * (fast ? 8 : 1);
            canvas._kbX = Math.min(canvas.width, Math.max(0, canvas._kbX + dx * step));
            canvas._kbY = Math.min(canvas.height, Math.max(0, canvas._kbY + dy * step));
            positionOverlayAt(canvas, overlay, canvas._kbX, canvas._kbY, 'keyboard');
            announcePosition(canvas, canvas._kbX, canvas._kbY);
        },

        paintAtKeyboardCursor(canvas) {
            pushUndoSnapshot(canvas);
            paintDab(canvas, canvas._kbX, canvas._kbY);
        },

        hasPixels(canvas) {
            const { data } = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height);
            for (let i = 3; i < data.length; i += 4) if (data[i] !== 0) return true;
            return false;
        },

        toPng(canvas) { return canvas.toDataURL('image/png').split(',')[1]; }
    };
})();
