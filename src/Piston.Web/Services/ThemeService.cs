// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.JSInterop;

namespace Piston.Web.Services;

/// <summary>
/// The theme preference ("system", "light" or "dark"), stored per browser by <c>js/theme.js</c>
/// so it applies before first paint. Shared so the header toggle and settings dialog stay in sync.
/// </summary>
public sealed class ThemeService(IJSRuntime js)
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static readonly IReadOnlyList<string> Preferences = [System, Light, Dark];

    private bool _loaded;

    public string Preference { get; private set; } = System;
    public bool IsCanvas { get; private set; }

    public event Action? Changed;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        IsCanvas = await js.InvokeAsync<bool>("pistonTheme.isCanvas");
        var saved = await js.InvokeAsync<string>("pistonTheme.get");
        if (IsCanvas || (saved != Preference && Preferences.Contains(saved)))
        {
            Preference = saved;
            Changed?.Invoke();
        }
    }

    public async Task SetAsync(string preference)
    {
        if (IsCanvas || !Preferences.Contains(preference) || preference == Preference)
        {
            return;
        }

        Preference = preference;
        Changed?.Invoke();
        await js.InvokeVoidAsync("pistonTheme.set", preference);
    }
}
