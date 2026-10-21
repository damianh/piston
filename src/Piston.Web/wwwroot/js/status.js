// Shows the run state outside the page: the tab title and a status dot on the favicon.
window.pistonStatus = (() => {
    const colors = { pass: "#3fb950", fail: "#f85149", busy: "#d29922" };
    let base = null;
    let current = null;

    async function baseIcon() {
        if (base === null) {
            try {
                base = await (await fetch("favicon.svg")).text();
            } catch {
                base = "";
            }
        }
        return base;
    }

    return {
        async set(title, state) {
            document.title = title;
            if (state === current) return;
            current = state;

            const svg = await baseIcon();
            const link = document.querySelector('link[rel="icon"][type="image/svg+xml"]');
            if (!svg || !link) return;

            const color = colors[state];
            const icon = color
                ? svg.replace("</svg>", `<circle cx="25" cy="25" r="6.5" fill="${color}" stroke="#0d1117" stroke-width="2"/></svg>`)
                : svg;
            link.href = "data:image/svg+xml," + encodeURIComponent(icon);
            // Browsers prefer the PNG icon; drop it so the SVG (and its status dot) is shown.
            document.querySelector('link[rel="icon"][type="image/png"]')?.remove();
        }
    };
})();
