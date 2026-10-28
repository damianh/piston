import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";

const script = await readFile(new URL("../../../src/Piston.Web/wwwroot/js/theme.js", import.meta.url), "utf8");

function theme(search, saved = "light") {
    let mode;
    let dark;
    let stored = saved;
    let mediaChanged;
    const window = { matchMedia: () => ({ matches: true, addEventListener: (_, fn) => { mediaChanged = fn; } }) };
    runInNewContext(script, {
        window, location: { search }, URLSearchParams,
        localStorage: { getItem: () => stored, setItem: (_, value) => { stored = value; } },
        document: { documentElement: { getAttribute: () => mode, classList: { toggle: (_, value) => { dark = value; } } } },
    });
    return { api: window.pistonTheme, dark: () => dark, stored: () => stored, mode: value => { mode = value; }, mediaChanged: () => mediaChanged() };
}

test("standalone theme retains saved preference and system following", () => {
    const page = theme("");
    assert.equal(page.dark(), false);
    page.api.set("dark");
    assert.equal(page.dark(), true);
    assert.equal(page.stored(), "dark");
    page.api.set("system");
    page.mediaChanged();
    assert.equal(page.dark(), true);
});

test("canvas follows live host color mode without overwriting standalone preference", () => {
    const page = theme("?pistonCanvas=1");
    assert.equal(page.api.isCanvas(), true);
    page.mode("dark");
    page.api.syncCanvas();
    assert.equal(page.dark(), true);
    page.api.set("light");
    assert.equal(page.dark(), true);
    assert.equal(page.stored(), "light");
    page.mode("light");
    page.api.syncCanvas();
    assert.equal(page.dark(), false);
});
