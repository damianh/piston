// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>A click on a test tree row.</summary>
/// <param name="Toggle">Ctrl or Cmd was held: add the row to, or remove it from, the selection.</param>
/// <param name="Range">Shift was held: select the rows from the anchor to this one.</param>
public sealed record TreeRowClick(string RowKey, TestNode Node, bool Toggle, bool Range);

/// <summary>A right-click on a test tree row, at viewport coordinates.</summary>
public sealed record TreeRowMenu(string RowKey, TestNode Node, double X, double Y);
