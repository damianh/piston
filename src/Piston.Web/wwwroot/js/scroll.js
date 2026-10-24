// Scrolls the selected row of a virtualized list into view, e.g. after another tab opens a test.
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
    }
};
