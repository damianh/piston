// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";
import { createDashboardCanvas } from "./provider.mjs";

const provider = createDashboardCanvas();
const emptyInput = { type: "object", properties: {}, additionalProperties: false };
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
        inputSchema: emptyInput,
        open: async ctx => {
            try {
                return await provider.open(ctx.instanceId, workingDirectory(ctx));
            } catch (error) {
                throw new CanvasError("dashboard_unavailable", error.message);
            }
        },
        actions: ["refresh", "run", "run_failed"].map(name => ({
            name,
            description: name === "refresh" ? "Rediscover the current worktree's dashboard without running tests."
                : name === "run" ? "Request a full test run." : "Request a run of the currently failed tests.",
            inputSchema: emptyInput,
            handler: async ctx => {
                try {
                    return await provider.action(ctx.instanceId, name, workingDirectory(ctx));
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
