import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { runInNewContext } from "node:vm";

const source = readFileSync(new URL("../src/Piston.Web/wwwroot/js/status.js", import.meta.url), "utf8");

test("an older favicon fetch cannot overwrite the latest state", async () => {
    const requests = [];
    const icon = {};
    const document = {
        title: "",
        querySelector: (selector) => selector.includes("svg") ? icon : null,
    };
    const window = {};
    runInNewContext(source, {
        window,
        document,
        fetch: () => new Promise(resolve => requests.push(resolve)),
    });

    const busy = window.pistonStatus.set("Running", "busy");
    const passed = window.pistonStatus.set("Passed", "pass");
    requests[1]({ text: async () => "<svg></svg>" });
    await passed;
    const passedIcon = icon.href;
    assert.match(decodeURIComponent(passedIcon), /#3fb950/);

    requests[0]({ text: async () => "<svg></svg>" });
    await busy;
    await window.pistonStatus.set("Passed", "pass");
    assert.equal(document.title, "Passed");
    assert.equal(icon.href, passedIcon);
});
