// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>How a changed line's verdict is shown.</summary>
/// <param name="Label">Short name, e.g. "uncovered".</param>
/// <param name="Glyph">A mark that tells verdicts apart without colour.</param>
/// <param name="Description">What the verdict means.</param>
/// <param name="RowClass">Tint for the line.</param>
/// <param name="GutterClass">Colour of the gutter bar.</param>
/// <param name="TextClass">Colour for the label.</param>
public sealed record VerdictStyle(
    string Label, string Glyph, string Description, string RowClass, string GutterClass, string TextClass);
