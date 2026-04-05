using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Piston.Protocol.Transports;

/// <summary>
/// Platform-aware factory for creating named pipe security descriptors.
/// On Windows, restricts pipe access to the current user only.
/// On non-Windows platforms, returns <see langword="null"/> (file-system permissions apply).
/// </summary>
internal static class PipeSecurityHelper
{
    /// <summary>
    /// Creates a <see cref="PipeSecurity"/> object granting <c>FullControl</c> only to
    /// the current Windows user SID.
    /// Returns <see langword="null"/> on non-Windows platforms.
    /// </summary>
    public static PipeSecurity? CreateCurrentUserOnly()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return CreateCurrentUserOnlyWindows();
    }

    [SupportedOSPlatform("windows")]
    private static PipeSecurity CreateCurrentUserOnlyWindows()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot determine current Windows user SID.");

        var security = new PipeSecurity();

        // Grant full control to the current user only.
        // Windows evaluates Deny rules before Allow rules, so adding an explicit Deny for
        // Everyone would block the current user (who is a member of Everyone) even with an
        // Allow rule present. Instead, we rely on the pipe's default behaviour: when an explicit
        // DACL is set, access is denied to anyone NOT listed — so a current-user-only Allow is
        // sufficient to restrict the pipe to the current user.
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return security;
    }
}
