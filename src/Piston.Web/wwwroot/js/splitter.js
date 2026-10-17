// Drag-to-resize for the Tests tab split view. The drag updates the first pane's flex-basis
// directly so it stays smooth while snapshots re-render the tree; the final value is reported
// back to .NET on release.
window.pistonSplitter = {
    attach(container, dotnetRef, min, max) {
        const onPointerDown = (e) => {
            const handle = e.target.closest('[data-splitter]');
            if (!handle || !container.contains(handle) || e.button !== 0) return;
            e.preventDefault();

            const pane = container.firstElementChild;
            const vertical = container.dataset.orientation === 'bottom';
            const previousUserSelect = document.body.style.userSelect;
            const previousCursor = document.body.style.cursor;
            let pct = null;

            handle.setPointerCapture(e.pointerId);
            handle.setAttribute('data-dragging', '');
            document.body.style.userSelect = 'none';
            document.body.style.cursor = vertical ? 'row-resize' : 'col-resize';

            const onMove = (ev) => {
                const rect = container.getBoundingClientRect();
                const raw = vertical
                    ? (ev.clientY - rect.top) / rect.height
                    : (ev.clientX - rect.left) / rect.width;
                pct = Math.min(max, Math.max(min, raw * 100));
                pane.style.flexBasis = pct + '%';
                handle.setAttribute('aria-valuenow', String(Math.round(pct)));
            };

            const onUp = (ev) => {
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
                handle.removeEventListener('pointercancel', onUp);
                if (handle.hasPointerCapture(ev.pointerId)) handle.releasePointerCapture(ev.pointerId);
                handle.removeAttribute('data-dragging');
                document.body.style.userSelect = previousUserSelect;
                document.body.style.cursor = previousCursor;
                if (pct !== null) dotnetRef.invokeMethodAsync('SetSplit', pct);
            };

            handle.addEventListener('pointermove', onMove);
            handle.addEventListener('pointerup', onUp);
            handle.addEventListener('pointercancel', onUp);
        };

        container.addEventListener('pointerdown', onPointerDown);
        container.__pistonSplitter = onPointerDown;
    },

    detach(container) {
        if (container && container.__pistonSplitter) {
            container.removeEventListener('pointerdown', container.__pistonSplitter);
            delete container.__pistonSplitter;
        }
    },
};
