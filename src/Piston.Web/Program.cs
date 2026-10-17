// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Piston.Web;
using Piston.Web.Models;
using Piston.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<EngineClientService>();
builder.Services.AddSingleton<ThemeService>();

// Tabs are re-created on switch; keep tree/selection UI state for the lifetime of the page.
builder.Services.AddSingleton<ViewState>();

await builder.Build().RunAsync();
