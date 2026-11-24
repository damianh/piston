// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Piston.Engine.Execution;

namespace Piston.Hosting;

internal static class DashboardDiscovery
{
    public static DashboardEndpoint Inspect(string path, ExecutionCoordinationDirectory? directory = null)
    {
        try
        {
            directory ??= ExecutionCoordinationDirectory.ResolveDefault();
            var identity = WorktreeIdentity.FromSolutionPath(path);
            var record = WorktreeOwnership.FindLiveOwner(directory, identity, reportErrors: true);
            if (record is null)
            {
                try
                {
                    var solution = Directory.Exists(path)
                        ? HostHelpers.ResolveSolutionPath(null, workingDirectory: path)
                        : HostHelpers.ResolveSolutionPath(new FileInfo(path));
                    return new("disconnected", "No live Piston owner.", Worktree: identity.RootPath, Solution: solution);
                }
                catch (InvalidOperationException ex)
                {
                    return new("disconnected", "No live Piston owner.", Worktree: identity.RootPath, SolutionError: ex.Message);
                }
            }
            if (record.Owner is not { } owner)
            {
                return new("error", "The live owner's metadata is unavailable. Retry discovery.", Worktree: identity.RootPath);
            }
            if (owner.DashboardUrl is not { } url)
            {
                return new("disabled", "The current owner has not published a dashboard. If this persists, upgrade Piston and restart the owner.",
                    Worktree: identity.RootPath);
            }
            if (!IsLoopbackUrl(url))
            {
                return new("error", "The owner advertised an invalid dashboard endpoint.", Worktree: identity.RootPath);
            }
            return new("connected", "Dashboard available.", url, identity.RootPath, owner.ProcessId, owner.AcquiredAt);
        }
        catch (Exception ex) when (ex is ExecutionCoordinationException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new("error", $"Dashboard discovery failed: {ex.Message}");
        }
    }

    internal static bool IsLoopbackUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "http" && uri.Port > 0 && uri.AbsolutePath == "/"
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
        && (uri.Host == "localhost" || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)));
}
