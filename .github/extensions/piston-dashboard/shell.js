// The shell outlives the engine; only the embedded document is replaced on owner changes.
const frame = document.getElementById("dashboard");
const connection = document.getElementById("connection");
const status = document.getElementById("status");
const retry = document.getElementById("retry");
const announcement = document.getElementById("announcement");
let identity;
let shownNavigation;
let loading = false;
let stopped = false;
let timer;

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

function disconnected(message) {
    if (identity) announcement.textContent = "Piston disconnected.";
    identity = undefined;
    frame.hidden = true;
    frame.removeAttribute("src");
    connection.hidden = false;
    if (status.textContent !== message) status.textContent = message;
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
            disconnected(state.message);
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
retry.addEventListener("click", () => { clearTimeout(timer); void update(); });
const observer = new MutationObserver(syncTheme);
observer.observe(document.documentElement, { attributes: true });
observer.observe(document.body, { attributes: true });
window.addEventListener("pagehide", () => { stopped = true; clearTimeout(timer); observer.disconnect(); });
void update();
