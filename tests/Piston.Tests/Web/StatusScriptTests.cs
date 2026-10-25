// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Jint;
using Xunit;

namespace Piston.Tests.Web;

/// <summary>Runs the dashboard's <c>wwwroot/js/status.js</c> in Jint against a stubbed DOM and <c>fetch</c>.</summary>
public class StatusScriptTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly string Source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "status.js"));

    [Fact]
    public void AnOlderFaviconFetch_CannotOverwriteTheLatestState()
    {
        var engine = new Jint.Engine();
        _ = engine.Execute("""
            var requests = [];
            var icon = {};
            var document = {
                title: "",
                querySelector: selector => selector.includes("svg") ? icon : null,
            };
            var window = {};
            function fetch() { return new Promise(resolve => requests.push(resolve)); }
            """);
        _ = engine.Execute(Source);

        var result = engine.Evaluate("""
            (async () => {
                const busy = window.pistonStatus.set("Running", "busy");
                const passed = window.pistonStatus.set("Passed", "pass");
                requests[1]({ text: async () => "<svg></svg>" });
                await passed;
                const passedIcon = icon.href;

                requests[0]({ text: async () => "<svg></svg>" });
                await busy;
                await window.pistonStatus.set("Passed", "pass");
                return { passedIcon, finalIcon: icon.href, title: document.title };
            })()
            """).UnwrapIfPromise(_ct).AsObject();

        var passedIcon = result.Get("passedIcon").AsString();
        Assert.Contains("#3fb950", Uri.UnescapeDataString(passedIcon));
        Assert.Equal(passedIcon, result.Get("finalIcon").AsString());
        Assert.Equal("Passed", result.Get("title").AsString());
    }
}
