// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";
import { createDashboardCanvas } from "./provider.mjs";

const provider = createDashboardCanvas();
const emptyInput = { type: "object", properties: {}, additionalProperties: false };
const changeProperties = {
    base: {
        type: "string", enum: ["working", "branch", "range"],
        description: "working: uncommitted changes against HEAD. branch: the branch's changes since it left baseRef "
            + "(default: the default branch). range: the commits in baseRef, e.g. HEAD~3..HEAD.",
    },
    baseRef: { type: "string", maxLength: 200, description: "Branch, commit or range for branch and range." },
};
const openInput = {
    type: "object",
    properties: {
        view: { type: "string", enum: ["dashboard", "changes"],
            description: "changes opens the coverage-aware diff (Coverage tab > Changes); dashboard opens the default view." },
        ...changeProperties,
    },
    additionalProperties: false,
};
const changesInput = { type: "object", properties: changeProperties, additionalProperties: false };
const descriptions = {
    refresh: "Rediscover the current worktree's dashboard without running tests.",
    run: "Request a full test run.",
    run_failed: "Request a run of the currently failed tests.",
    show_changes: "Show the coverage-aware diff of the changes, each changed line coloured by what the tests prove "
        + "about it. Only navigates the panel; it never runs tests.",
};
const workingDirectory = ctx => {
    const cwd = ctx.session?.workingDirectory;
    if (!cwd) throw new CanvasError("workspace_unavailable", "The runtime did not supply the active working directory.");
    return cwd;
};

await joinSession({
    canvases: [createCanvas({
        id: "piston-dashboard",
        displayName: "Piston",
        description: "Inspect the current worktree's Piston dashboard without starting an engine or a test run.",
        inputSchema: openInput,
        open: async ctx => {
            try {
                return await provider.open(ctx.instanceId, workingDirectory(ctx), ctx.input);
            } catch (error) {
                throw new CanvasError("dashboard_unavailable", error.message);
            }
        },
        actions: Object.entries(descriptions).map(([name, description]) => ({
            name,
            description,
            inputSchema: name === "show_changes" ? changesInput : emptyInput,
            handler: async ctx => {
                try {
                    return await provider.action(ctx.instanceId, name, workingDirectory(ctx), ctx.input);
                } catch (error) {
                    throw new CanvasError("dashboard_action_failed", error.message);
                }
            },
        })),
        onClose: ctx => provider.close(ctx.instanceId),
    })],
});

for (const signal of ["SIGTERM", "SIGINT"]) {
    process.once(signal, () => { void provider.dispose().finally(() => process.exit(0)); });
}
