// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

internal interface ICoberturaParser
{
    /// <summary>
    /// Parses a Cobertura XML file and returns a structured coverage report.
    /// </summary>
    /// <param name="coberturaXmlPath">Absolute path to the <c>coverage.cobertura.xml</c> file.</param>
    CoberturaReport Parse(string coberturaXmlPath);
}
