// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class ContentSecurityPolicyTests
{
    [Fact]
    public void Build_AllowsInlineImportMapByHash_AndNotScriptsWithSrc()
    {
        const string importMap = "{\n  \"imports\": {}\n}";
        var html = $"""
            <head>
            <script src="js/theme.js"></script>
            <script type="importmap">{importMap}</script>
            </head>
            """;

        var policy = ContentSecurityPolicy.Build(html);

        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(importMap)));
        Assert.Contains($"script-src 'self' 'wasm-unsafe-eval' 'sha256-{expected}'", policy, StringComparison.Ordinal);
        _ = Assert.Single(ContentSecurityPolicy.InlineScriptHashes(html));
        Assert.DoesNotContain("'unsafe-inline'; ", policy.Split("style-src")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithoutIndex_StillRestrictsScripts()
    {
        var policy = ContentSecurityPolicy.Build(null);

        Assert.Contains("script-src 'self' 'wasm-unsafe-eval';", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("frame-ancestors", policy, StringComparison.Ordinal);
    }
}
