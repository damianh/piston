// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Request a page of the session timeline.</summary>
/// <param name="AfterSequence">Return only runs requested after this sequence (the last entry of the previous page); null starts from the oldest.</param>
public sealed record GetSessionTimelineCommand(long? AfterSequence = null);
