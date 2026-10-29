// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>
/// Where a dashboard URL asks to open, e.g. <c>?tab=coverage&amp;mode=changes&amp;base=branch&amp;ref=main</c>.
/// Unrecognised or malformed values are ignored.
/// </summary>
/// <param name="Tab">The tab's name, lower case, e.g. <c>coverage</c>.</param>
/// <param name="Mode">The Coverage tab's mode.</param>
/// <param name="Base"><c>working</c>, <c>branch</c> or <c>range</c>.</param>
/// <param name="BaseRef">The branch, commit or range to compare against.</param>
public sealed record DashboardLink(string? Tab, CoverageMode? Mode, string? Base, string? BaseRef)
{
    public static DashboardLink Parse(string? query)
    {
        string? tab = null, @base = null, baseRef = null;
        CoverageMode? mode = null;
        foreach (var pair in (query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? "" : Decode(pair[(equals + 1)..]);
            if (value is null)
            {
                continue;
            }

            switch (key)
            {
                case "tab" when value.Length is > 0 and <= 32 && value.All(char.IsAsciiLetter):
                    tab = value.ToLowerInvariant();
                    break;
                case "mode" when Enum.TryParse<CoverageMode>(value, ignoreCase: true, out var parsed)
                    && Enum.IsDefined(parsed) && !value.Any(char.IsAsciiDigit):
                    mode = parsed;
                    break;
                case "base" when ChangeDiffModel.Bases.Contains(value):
                    @base = value;
                    break;
                case "ref" when value.Length is > 0 and <= ChangeDiffModel.MaxBaseRefLength && !value.Any(char.IsControl):
                    baseRef = value;
                    break;
            }
        }

        return new(tab, mode, @base, baseRef);

        static string? Decode(string text)
        {
            try
            {
                return Uri.UnescapeDataString(text.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
    }
}
