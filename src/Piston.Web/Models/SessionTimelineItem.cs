// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>An edit or a run on the session timeline.</summary>
public abstract record SessionTimelineItem(DateTimeOffset At);
