// The shell outlives the engine; only the embedded document is replaced on owner changes.
const frame = document.getElementById("dashboard");
const connection = document.getElementById("connection");
const status = document.getElementById("status");
const dashboardDisabled = document.getElementById("dashboard-disabled");
const retry = document.getElementById("retry");
const startForm = document.getElementById("start-form");
const solution = document.getElementById("solution");
const solutions = document.getElementById("solutions");
const solutionHelp = document.getElementById("solution-help");
const solutionDiscovery = document.getElementById("solution-discovery");
const start = document.getElementById("start");
const startError = document.getElementById("start-error");
const announcement = document.getElementById("announcement");
let identity;
let shownNavigation;
let loading = false;
let stopped = false;
let timer;
let starting = false;
let suggested = false;
let solutionsLoaded = false;
let solutionsLoading = false;

const attributes = ["data-color-mode", "data-dark-theme", "data-light-theme", "data-theme-source", "data-theme-tone", "data-visual-mode"];
const tokens = ["--background-color-default", "--border-color-default", "--text-color-default",
    "--text-color-muted", "--color-focus-outline", "--color-white", "--true-color-red",
    "--true-color-red-muted", "--true-color-blue", "--true-color-blue-muted", "--font-sans",
    "--font-mono", "--font-weight-semibold", "--text-body-medium", "--leading-body-medium"];

function syncTheme() {
    const root = frame.contentDocument?.documentElement;
    if (!root || !frame.contentDocument.body) return;
    const source = document.documentElement;
    const style = getComputedStyle(source);
    root.setAttribute("data-piston-canvas", "");
    for (const attr of attributes) {
        const value = source.getAttribute(attr) ?? document.body.getAttribute(attr);
        if (value !== null) root.setAttribute(attr, value);
        else root.removeAttribute(attr);
    }
    root.classList.toggle("pointer-on-hover", source.classList.contains("pointer-on-hover") || document.body.classList.contains("pointer-on-hover"));
    for (const token of tokens) {
        const value = style.getPropertyValue(token);
        if (value.trim()) root.style.setProperty(token, value);
        else root.style.removeProperty(token);
    }
    frame.contentWindow.pistonTheme?.syncCanvas();
}

function disconnected(message, state) {
    if (identity) announcement.textContent = "Piston disconnected.";
    identity = undefined;
    frame.hidden = true;
    frame.removeAttribute("src");
    connection.hidden = false;
    const displayed = starting && state?.state === "disconnected" ? "Starting Piston..." : message;
    if (status.textContent !== displayed) status.textContent = displayed;
    dashboardDisabled.hidden = state?.state !== "disabled";
    startForm.hidden = state?.state !== "disconnected";
    if (state?.state === "disconnected" && !solutionsLoaded) void loadSolutions();
    if (!suggested && state?.solution) {
        solution.value = state.solution;
        suggested = true;
    }
    solutionHelp.textContent = state?.solutionError || "Choose a solution or enter a path inside this worktree.";
}

async function loadSolutions() {
    if (solutionsLoading || stopped) return;
    solutionsLoading = true;
    solutionDiscovery.textContent = "Looking for solutions...";
    try {
        const response = await fetch("solutions", { cache: "no-store", signal: AbortSignal.timeout(12000) });
        const result = await response.json();
        if (!response.ok) throw new Error(result.error || `Discovery failed (HTTP ${response.status}).`);
        solutions.replaceChildren(...result.solutions.map(value => {
            const option = document.createElement("option");
            option.value = value;
            return option;
        }));
        solutionsLoaded = true;
        solutionDiscovery.textContent = result.solutions.length
            ? `${result.solutions.length} worktree solution${result.solutions.length === 1 ? "" : "s"} found. Type to filter.`
            : "No solutions found. You can enter a path manually.";
    } catch (error) {
        solutionsLoaded = true;
        solutionDiscovery.textContent = `${error.message} Enter a path manually or retry the connection.`;
    } finally {
        solutionsLoading = false;
    }
}

async function update() {
    if (loading || stopped) return;
    loading = true;
    retry.disabled = true;
    try {
        const response = await fetch("state", { cache: "no-store", signal: AbortSignal.timeout(7000) });
        if (!response.ok) throw new Error(`Connection inspection returned HTTP ${response.status}.`);
        const state = await response.json();
        if (state.state !== "connected") {
            disconnected(state.message, state);
        } else if (identity !== state.identity || shownNavigation !== state.navigation?.seq) {
            const reconnected = identity !== state.identity;
            identity = state.identity;
            shownNavigation = state.navigation?.seq;
            const view = state.navigation?.query ? `&${state.navigation.query}` : "";
            status.textContent = "Loading the current worktree's dashboard...";
            frame.src = `/?pistonCanvas=1&owner=${encodeURIComponent(identity)}${view}`;
            frame.hidden = false;
            connection.hidden = true;
            if (reconnected) announcement.textContent = "Connected to Piston.";
        }
    } catch (error) {
        disconnected(`Could not inspect Piston: ${error.message} Retry the connection; if the extension restarted, reopen this canvas.`);
    } finally {
        loading = false;
        retry.disabled = false;
        if (!stopped) timer = setTimeout(update, 3000);
    }
}

frame.addEventListener("load", syncTheme);
retry.addEventListener("click", () => { solutionsLoaded = false; clearTimeout(timer); void update(); });
solution.addEventListener("focus", () => { if (!solutionsLoading) void loadSolutions(); });
solution.addEventListener("input", () => { suggested = true; });
startForm.addEventListener("submit", async event => {
    event.preventDefault();
    if (starting) return;
    starting = true;
    start.disabled = true;
    solution.disabled = true;
    startError.hidden = true;
    status.textContent = "Starting Piston...";
    try {
        const response = await fetch("start", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ solution: solution.value }),
        });
        const result = await response.json();
        if (!response.ok) throw new Error(result.error || `Startup failed (HTTP ${response.status}).`);
        clearTimeout(timer);
        await update();
    } catch (error) {
        status.textContent = "Piston could not start.";
        startError.textContent = error.message;
        startError.hidden = false;
    } finally {
        starting = false;
        start.disabled = false;
        solution.disabled = false;
    }
});
const observer = new MutationObserver(syncTheme);
observer.observe(document.documentElement, { attributes: true });
observer.observe(document.body, { attributes: true });
window.addEventListener("pagehide", () => { stopped = true; clearTimeout(timer); observer.disconnect(); });
void update();
