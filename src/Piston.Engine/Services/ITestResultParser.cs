// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface ITestResultParser
{
    IReadOnlyList<TestSuite> Parse(string trxFilePath);
}
