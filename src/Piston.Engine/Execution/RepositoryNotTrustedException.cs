// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

public sealed class RepositoryNotTrustedException(string repositoryPath)
    : InvalidOperationException(
        $"Repository '{repositoryPath}' is not trusted for builds and tests. Ask the user to approve it in the Piston dashboard or run 'piston trust'.");
