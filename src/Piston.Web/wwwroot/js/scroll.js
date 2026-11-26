// Scrolls the selected row of a windowed list into view, e.g. after another tab opens a test.
// A row outside the rendered window is not in the DOM, so the list is scrolled to its index instead.
window.pistonScroll = {
    revealRow(scroller, index, rowHeight) {
        if (!scroller) return;
        const row = scroller.querySelector("[data-selected]");
        if (row) {
            row.scrollIntoView({ block: "center" });
        } else if (index >= 0) {
            scroller.scrollTop = Math.max(0, index * rowHeight - scroller.clientHeight / 2 + rowHeight / 2);
        }
    },

    // Reports a scroller's scroll position and height to .NET, at most once per frame, so a windowed list
    // renders the rows in view.
    observe(scroller, dotnet) {
        if (!scroller) return;
        let pending = false;
        const report = () => {
            if (pending) return;
            pending = true;
            requestAnimationFrame(() => {
                pending = false;
                dotnet.invokeMethodAsync("SetViewport", scroller.scrollTop, scroller.clientHeight).catch(() => {});
            });
        };
        const resize = new ResizeObserver(report);
        resize.observe(scroller);
        scroller.addEventListener("scroll", report, { passive: true });
        scroller._pistonViewport = () => {
            resize.disconnect();
            scroller.removeEventListener("scroll", report);
        };
        report();
    },

    unobserve(scroller) {
        scroller?._pistonViewport?.();
        if (scroller) delete scroller._pistonViewport;
    },

    // Scrolls an element, e.g. a timeline entry another entry links to, into view.
    revealElement(id) {
        document.getElementById(id)?.scrollIntoView({ block: "center" });
    }
};
