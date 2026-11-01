// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// A parsed MTP test result produced by <see cref="MtpStdoutParser"/>.
/// </summary>
internal sealed record MtpParsedResult(
    string FullyQualifiedName,
    TestStatus Status,
    TimeSpan Duration,
    string? Source,
    string? ErrorMessage,
    string? StackTrace,
    string? Output = null,
    string? ErrorOutput = null,
    string? TargetFramework = null)
{
    /// <summary>Identifies the result within one run: a multi-targeted project reports each test once per framework.</summary>
    public string Key => TargetFramework is null ? FullyQualifiedName : $"{FullyQualifiedName} [{TargetFramework}]";
}
