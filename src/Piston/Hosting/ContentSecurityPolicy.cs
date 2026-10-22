// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Piston.Hosting;

/// <summary>
/// The dashboard's <c>Content-Security-Policy</c>. Scripts load only from the daemon itself; the inline
/// import map the Blazor build writes into <c>index.html</c> is allowed by hash, and WebAssembly needs
/// <c>'wasm-unsafe-eval'</c>. Inline styles stay allowed: components set widths through <c>style</c>.
/// The policy sets no <c>frame-ancestors</c>, so tools that embed the dashboard keep working.
/// </summary>
internal static partial class ContentSecurityPolicy
{
    /// <summary>Builds the policy for the dashboard page <paramref name="indexHtml"/> (or none when it is missing).</summary>
    public static string Build(string? indexHtml)
    {
        var scriptSources = new List<string> { "'self'", "'wasm-unsafe-eval'" };
        scriptSources.AddRange(InlineScriptHashes(indexHtml ?? string.Empty));

        return string.Join("; ",
            "default-src 'self'",
            $"script-src {string.Join(' ', scriptSources)}",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "font-src 'self' data:",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'");
    }

    /// <summary>Reads <c>index.html</c> from <paramref name="webRoot"/>, or <see langword="null"/> when it is absent.</summary>
    public static string? ReadIndex(Microsoft.Extensions.FileProviders.IFileProvider webRoot)
    {
        var file = webRoot.GetFileInfo("index.html");
        if (!file.Exists || file.IsDirectory)
        {
            return null;
        }

        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static IEnumerable<string> InlineScriptHashes(string html) =>
        InlineScript().Matches(html)
            .Select(match => match.Groups["body"].Value)
            .Where(body => body.Length > 0)
            .Select(body => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}'")
            .Distinct(StringComparer.Ordinal);

    [GeneratedRegex(@"<script(?![^>]*\ssrc\s*=)[^>]*>(?<body>.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();
}
