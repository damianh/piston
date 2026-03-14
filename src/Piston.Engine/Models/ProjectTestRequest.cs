namespace Piston.Engine.Models;

public sealed record ProjectTestRequest(
    string ProjectPath,
    string? Filter,
    bool CollectCoverage);
