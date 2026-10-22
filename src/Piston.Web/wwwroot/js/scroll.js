// Scrolls the selected row inside a container into view, e.g. after another tab opens a test.
window.pistonScroll = {
    revealSelected(container) {
        container?.querySelector("[data-selected]")?.scrollIntoView({ block: "center" });
    }
};
