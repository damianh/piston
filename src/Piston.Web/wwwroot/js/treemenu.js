// The test tree's context menu: keeps it inside the viewport, moves focus between its items with the arrow keys,
// closes it on Escape, Tab, a click outside, scrolling, resizing or leaving the window, and opens it from the
// keyboard (Shift+F10 or the Menu key) at the selected row instead of the browser's own menu.
window.pistonTreeMenu = {
    observeKeys(dotnet) {
        let keyedAt = 0;
        const editable = (el) => el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));
        const onKey = (e) => {
            const menuKey = e.key === "ContextMenu" || (e.key === "F10" && e.shiftKey);
            if (!menuKey || editable(e.target) || e.target.closest?.("[role=menu], dialog")) return;
            e.preventDefault();
            keyedAt = Date.now();
            dotnet.invokeMethodAsync("OpenMenuAtSelection").catch(() => {});
        };
        // The keyboard still raises a contextmenu event; suppress the browser's menu for it.
        const onMenu = (e) => {
            if (Date.now() - keyedAt < 1000) {
                keyedAt = 0;
                e.preventDefault();
            }
        };
        document.addEventListener("keydown", onKey, true);
        document.addEventListener("contextmenu", onMenu, true);
        window._pistonTreeMenuKeys = () => {
            document.removeEventListener("keydown", onKey, true);
            document.removeEventListener("contextmenu", onMenu, true);
        };
    },

    unobserveKeys() {
        window._pistonTreeMenuKeys?.();
        delete window._pistonTreeMenuKeys;
    },

    // Where to open the menu for the keyboard: just below the label of the selected row at `index`. A row outside
    // the windowed list's rendered rows is not in the DOM, so the list scrolls to its index and measures from that.
    selectedRowPoint(scroller, index, rowHeight) {
        if (!scroller || index < 0) return null;
        const top = index * rowHeight;
        if (top < scroller.scrollTop) scroller.scrollTop = top;
        else if (top + rowHeight > scroller.scrollTop + scroller.clientHeight) scroller.scrollTop = top + rowHeight - scroller.clientHeight;
        const rect = scroller.getBoundingClientRect();
        return [rect.left + 32, rect.top + top - scroller.scrollTop + rowHeight];
    },

    open(menu, x, y, dotnet) {
        if (!menu) return;
        const margin = 4;
        const { width, height } = menu.getBoundingClientRect();
        menu.style.left = `${Math.max(margin, Math.min(x, window.innerWidth - width - margin))}px`;
        menu.style.top = `${Math.max(margin, y + height + margin > window.innerHeight ? y - height : y)}px`;
        menu.style.visibility = "visible";

        const previous = document.activeElement;
        const items = () => [...menu.querySelectorAll("[role=menuitem]:not([disabled])")];
        items()[0]?.focus();

        const close = () => dotnet.invokeMethodAsync("CloseMenu").catch(() => {});
        const onKey = (e) => {
            const all = items();
            const index = all.indexOf(document.activeElement);
            switch (e.key) {
                case "ArrowDown": all[(index + 1) % all.length]?.focus(); break;
                case "ArrowUp": all[(index - 1 + all.length) % all.length]?.focus(); break;
                case "Home": all[0]?.focus(); break;
                case "End": all[all.length - 1]?.focus(); break;
                case "Escape": case "Tab": close(); break;
                case "Enter": case " ": e.stopPropagation(); return; // the focused button clicks itself
                default: e.stopPropagation(); return; // keep dashboard shortcuts from firing behind the menu
            }
            e.preventDefault();
            e.stopPropagation();
        };
        const onPointer = (e) => { if (!menu.contains(e.target)) close(); };
        const onScroll = (e) => { if (!menu.contains(e.target)) close(); };
        menu.addEventListener("keydown", onKey);
        document.addEventListener("pointerdown", onPointer, true);
        document.addEventListener("scroll", onScroll, true);
        window.addEventListener("resize", close);
        window.addEventListener("blur", close);
        menu._pistonClose = () => {
            menu.removeEventListener("keydown", onKey);
            document.removeEventListener("pointerdown", onPointer, true);
            document.removeEventListener("scroll", onScroll, true);
            window.removeEventListener("resize", close);
            window.removeEventListener("blur", close);
            // The menu may already be gone, leaving focus on the body; give it back so shortcuts keep working.
            const lost = !document.activeElement || document.activeElement === document.body || menu.contains(document.activeElement);
            if (lost && previous?.isConnected) previous.focus();
        };
    },

    close(menu) {
        menu?._pistonClose?.();
        if (menu) delete menu._pistonClose;
    },

    copy(text) {
        return navigator.clipboard.writeText(text);
    }
};
