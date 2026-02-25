# Piston — Design Specification & Implementation Plan

> A .NET TUI continuous test runner. Think NCrunch, but in your terminal.

---

## 1. Product Vision

**Piston** watches a .NET solution for file changes, automatically rebuilds affected projects, runs tests, and displays results in a rich terminal UI — all in a continuous loop while the developer codes.

### What Piston Is
- A terminal-based continuous test runner for .NET
- A developer productivity tool that gives instant feedback on test health
- A single-binary CLI tool (`piston` or `piston ./path/to/Solution.sln`)
- Keyboard-driven with a rich TUI (panels, trees, status bars)

### What Piston Is NOT
- Not an IDE plugin (though that could come later)
- Not a CI tool — it's for local dev loops
- Not a test framework — it runs xUnit/NUnit/MSTest via `dotnet test`
- Not a code coverage tool (future consideration)

---

## 2. Technology Choices

| Technology | Choice | Justification |
|---|---|---|
| Runtime | **.NET 10 / C# 14** | Latest, best perf, AOT-friendly |
| TUI Framework | **SharpConsoleUI** (Spectre.Console-based) | Windowing framework built on Spectre.Console. Provides WindowManager, TreeControl, ListControl, focus management, double-buffered rendering. Beautiful Spectre.Console output quality with real TUI interactivity. See framework comparison below. |
| File Watching | `FileSystemWatcher` | Built-in, cross-platform, sufficient for watching .cs/.csproj changes |
| Build Execution | `dotnet build` via `Process` | Simplest, most reliable. Captures stdout/stderr for error reporting. |
| Test Execution | `dotnet test` via `Process` | Framework-agnostic (xUnit, NUnit, MSTest). TRX logger for structured results. |
| Result Parsing | TRX (XML) | Standard VSTest output format. Rich structured data (duration, output, stack traces). |
| Dependency Injection | `Microsoft.Extensions.DependencyInjection` | Standard .NET DI. Lightweight, familiar. |
| Logging | `Microsoft.Extensions.Logging` | Standard, can route to file for debugging TUI apps |
| Configuration | `Microsoft.Extensions.Configuration` | For optional `.piston.json` config file |

### TUI Framework Comparison

Three viable .NET TUI frameworks were evaluated:

#### Terminal.Gui v2
- **GitHub**: [gui-cs/Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) — ⭐ 10.8k stars, 126 contributors, 8,500+ commits
- **NuGet**: 1.5M total downloads, ~6k/day. v2 is in alpha (2.0.0-alpha/develop builds), v1.19.0 is stable.
- **Status**: Very actively developed (commits daily on v2_develop branch, Feb 2026). v2 is recommended for new projects.
- **Architecture**: Full ncurses-style TUI application model. Owns the terminal — provides its own main loop, event system, and rendering pipeline.
- **Key controls**: `Window`, `FrameView`, `TreeView`, `ListView`, `TableView`, `TextView`, `MenuBar`, `StatusBar`, `Dialog`, `Wizard`, `ColorPicker`, `TabView`, `ProgressBar` — comprehensive widget set.
- **Layout**: Flexible `Pos`/`Dim` system for relative and absolute positioning. Responsive to terminal resize.
- **Keyboard/Mouse**: Full support, including key bindings, focus management, drag & drop.
- **Themes**: Built-in theming with TrueColor support. Configurable via JSON.
- **License**: MIT
- **.NET compat**: Targets netstandard2.0 + net6.0 + net8.0 (will work on .NET 10 via compat)
- **Fit for Piston**: ✅ **Excellent** — has every control we need (TreeView, panels, status bar, keyboard nav, real-time updates)

#### Spectre.Console
- **GitHub**: [spectreconsole/spectre.console](https://github.com/spectreconsole/spectre.console) — ⭐ 11.2k stars, 132 contributors
- **NuGet**: Very widely used (10.2k dependents). .NET Foundation project.
- **Status**: Stable, actively maintained.
- **Architecture**: Output-oriented rendering library — designed for writing rich formatted output (tables, trees, panels, progress bars) to the console. NOT a persistent TUI framework.
- **Key features**: Beautiful markup, tables, trees, panels, progress bars, `Live` display, `Layout` widget, status spinners, prompts.
- **Live rendering**: `AnsiConsole.Live()` can update a renderable in-place. `Layout` widget can create multi-panel layouts. However:
  - ⚠️ **No built-in keyboard input handling** in live mode — you'd have to roll your own input loop
  - ⚠️ **No focus management** between panels
  - ⚠️ **No interactive controls** (no text input, no selectable tree, no scrollable lists with keyboard nav)
  - ⚠️ Layout + Live is primarily for display, not interactive applications
- **License**: MIT
- **Fit for Piston**: ⚠️ **Partial** — great for rendering pretty output but would require significant custom code to build an interactive TUI. No keyboard-navigable tree view, no panel focus management. Would essentially mean building a TUI framework on top of Spectre's rendering.

#### SharpConsoleUI (ConsoleEx)
- **GitHub**: [nickprotop/ConsoleEx](https://github.com/nickprotop/ConsoleEx) — ⭐ 33 stars, 1 contributor, 679 commits
- **NuGet**: `SharpConsoleUI` v2.4.32. Last update: 2025.
- **Status**: Active single-developer project with substantial commit history. Used in production by [lazynuget](https://github.com/nickprotop/lazynuget).
- **Architecture**: A **TUI windowing framework built on top of Spectre.Console**. Adds the interactive layer that Spectre.Console lacks — multi-window management, keyboard input handling, focus management, and double-buffered rendering.
- **Key features**:
  - `WindowManager` with independent window threads
  - `TreeControl`, `ListControl`, `TextArea` — interactive controls with keyboard nav
  - Focus management between windows/controls
  - Double-buffered rendering (flicker-free)
  - Mouse support
  - Inherits Spectre.Console's rendering quality (colors, markup, Unicode)
- **Strengths**:
  - ✅ Builds on Spectre.Console — gets its beautiful rendering for free
  - ✅ Real windowing system with focus management
  - ✅ TreeControl and ListControl — exactly what Piston needs
  - ✅ Proven in a real app (lazynuget — TUI NuGet package manager)
  - ✅ 679 commits suggests serious development effort
  - ✅ MIT licensed
- **Weaknesses**:
  - ⚠️ **Single developer** — bus factor of 1, no community beyond the author
  - ⚠️ **33 stars** — very small user base, limited battle-testing
  - ⚠️ **Currently targets .NET 9** — .NET 10 compat needs verification
  - ⚠️ **No documentation** beyond examples — learning relies on reading source code
  - ⚠️ API stability unknown — single developer may make breaking changes
- **License**: MIT
- **Fit for Piston**: ⚠️ **Interesting but risky** — provides exactly the controls Piston needs on top of Spectre.Console's excellent rendering, but the single-developer bus factor and tiny community are concerns for a tool we want to maintain long-term.

#### lazydotnet (reference implementation — Spectre.Console-based TUI)
- **GitHub**: [ckob/lazydotnet](https://github.com/ckob/lazydotnet) — ⭐ 97 stars, .NET 10
- **What it is**: A TUI for managing .NET solutions (build, test, manage packages, run projects). **Directly relevant to Piston** — it has a test runner tab with hierarchical test tree, test detail views, vim-style keybindings, solution explorer.
- **TUI approach**: Built entirely with **Spectre.Console** — no Terminal.Gui, no SharpConsoleUI. The developer built their own input loop, layout system, and keyboard navigation on top of Spectre.Console's `Live` rendering.
- **Key insight**: **Proves that Spectre.Console CAN be used for a full interactive TUI**, but requires building a substantial custom input/layout framework. This is both a strength (full control over UX) and a weakness (significant upfront effort, custom code to maintain).
- **Published as**: A dotnet tool (`dotnet tool install lazydotnet`)
- **Relevance**: Study this project's architecture if we go the Spectre.Console route. Shows what's possible but also shows how much custom work is involved.

#### Consolonia (Avalonia-based)
- **GitHub**: [Consolonia/Consolonia](https://github.com/Consolonia/Consolonia) — ⭐ 750 stars, 6 contributors, 342 commits
- **NuGet**: 22.9k total downloads. Beta status (11.3.9-beta). Last update: Dec 2025.
- **Status**: Beta. Interesting concept but much less mature than Terminal.Gui.
- **Architecture**: Runs Avalonia UI framework inside the terminal. Uses XAML for UI definition, data binding, templates, styles — full Avalonia programming model.
- **Key features**: Avalonia's full control set rendered in text mode. XAML + data binding. Theming via Avalonia styles. Cross-platform.
- **Strengths**: If you know Avalonia, the development model is familiar. Data binding is first-class. Rich control set inherited from Avalonia.
- **Weaknesses**:
  - ⚠️ **Beta** — not yet stable, only 6 contributors
  - ⚠️ **Heavy dependency** — brings in the entire Avalonia framework as a dependency
  - ⚠️ **Small community** — limited ecosystem, few examples, hard to get help
  - ⚠️ **Performance unknown** — running a full GUI framework in text mode may have overhead
  - ⚠️ Targets net8.0 — .NET 10 compat untested
- **License**: MIT
- **Fit for Piston**: ⚠️ **Risky** — interesting tech but too immature for a production tool. The Avalonia dependency is heavy for a CLI tool.

### Decision: **SharpConsoleUI + Spectre.Console**

| Criteria | Terminal.Gui v2 | SharpConsoleUI | Spectre (DIY) | Consolonia |
|----------|:---:|:---:|:---:|:---:|
| Persistent TUI layout | ✅ | ✅ | ⚠️ Build yourself | ✅ |
| Interactive TreeView | ✅ | ✅ TreeControl | ❌ Build yourself | ✅ |
| Keyboard navigation | ✅ | ✅ | ❌ Build yourself | ✅ |
| Focus management | ✅ | ✅ WindowManager | ❌ Build yourself | ✅ |
| Status bar | ✅ | ✅ | ❌ Build yourself | ✅ |
| Real-time updates | ✅ | ✅ | ✅ Live | ✅ |
| Rendering quality | ✅ Good | ✅ Spectre-quality | ✅ Spectre-quality | ✅ |
| Maturity | ✅ 8.5k commits, 126 contributors | ⚠️ 679 commits, 1 dev | ✅ Stable (rendering) | ⚠️ Beta |
| Community | ✅ 10.8k stars | ⚠️ 33 stars | ✅ 11.2k stars | ⚠️ 750 stars |
| Dependency weight | ✅ Light | ✅ Light (Spectre) | ✅ Light | ❌ Heavy (Avalonia) |
| .NET 10 compat | ✅ | ⚠️ Targets .NET 9 | ✅ | ⚠️ Unknown |
| Custom work needed | ✅ Low | ✅ Low | ❌ High | ✅ Low |
| Bus factor risk | ✅ Low | ❌ High (1 dev) | ✅ Low | ⚠️ Medium |

**SharpConsoleUI** is the chosen framework. It provides Spectre.Console's beautiful rendering quality plus the interactive windowing layer we need (TreeControl, ListControl, focus management, keyboard handling, double-buffered rendering). The single-developer risk is mitigated by keeping a TUI-agnostic core (`Piston.Core`) and thin UI wrappers so we could swap frameworks if needed.

> **Note**: SharpConsoleUI currently targets .NET 9. We'll need to verify .NET 10 compatibility early in Phase 1. If issues arise, we can reference the source directly or contribute a .NET 10 target upstream.

---

## 3. Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                   PistonApp (TUI)                    │
│  ┌──────────┐  ┌──────────┐  ┌───────────────────┐  │
│  │ TestTree  │  │ Detail   │  │ StatusBar         │  │
│  │ View      │  │ Panel    │  │                   │  │
│  └──────────┘  └──────────┘  └───────────────────┘  │
└────────────────────┬────────────────────────────────┘
                     │ subscribes to state changes
              ┌──────┴──────┐
              │  PistonState │  (observable state store)
              └──────┬──────┘
                     │ updated by
           ┌─────────┴─────────┐
           │  PistonOrchestrator │  (pipeline coordinator)
           └─────────┬─────────┘
                     │ drives
        ┌────────────┼────────────┐
        ▼            ▼            ▼
 ┌─────────────┐ ┌──────────┐ ┌───────────┐
 │FileWatcher  │ │Build     │ │TestRunner  │
 │Service      │ │Service   │ │Service     │
 │             │ │          │ │            │
 │ watches .cs │ │ dotnet   │ │ dotnet test│
 │ debounces   │ │ build    │ │ TRX parse  │
 └─────────────┘ └──────────┘ └───────────┘
```

### Data Flow
1. **FileWatcherService** detects `.cs`/`.csproj`/`.sln` changes
2. **Debouncer** coalesces rapid changes (300ms window)
3. **PistonOrchestrator** triggers build
4. **BuildService** runs `dotnet build`, reports success/failure
5. On success → **TestRunnerService** runs `dotnet test --logger "trx;LogFileName=results.trx"`
6. **TestResultParser** parses TRX XML into `TestResult` models
7. **PistonState** is updated with new results
8. **TUI** re-renders from state

### Key Design Principles
- **Event-driven**: Components communicate via events/observables, not polling
- **Async throughout**: All I/O is async, UI thread stays responsive
- **Cancellable**: A new file change cancels in-progress build/test runs
- **Stateless services**: All mutable state lives in `PistonState`

---

## 4. TUI Layout

```
┌─ Piston ─────────────────────────────────────────────────────────────┐
│ ● WATCHING  │  MyProject.sln  │  Last run: 12:34:56                  │
├──────────────────────────┬───────────────────────────────────────────┤
│  Tests                   │  Test Detail                              │
│                          │                                           │
│  ▼ MyProject.Tests       │  MyProject.Tests.MathTests               │
│    ▼ MathTests           │  .AddNumbers_ReturnsCorrectSum            │
│      ✓ AddNumbers_Ret... │                                           │
│      ✗ Subtract_Throws   │  Status: FAILED                          │
│      ✓ Multiply_Works    │  Duration: 23ms                          │
│    ▼ StringTests         │                                           │
│      ✓ Concat_Works      │  Error:                                  │
│      ● Trim_Pending      │  Assert.Equal() Failure                  │
│                          │  Expected: 5                              │
│  ▼ OtherProject.Tests   │  Actual:   3                              │
│    ▼ ApiTests            │                                           │
│      ✓ GetUser_Returns   │  Stack Trace:                            │
│      ✓ PostUser_Creates  │  at MathTests.Subtract_Throws()          │
│                          │     in MathTests.cs:line 42               │
│                          │                                           │
├──────────────────────────┴───────────────────────────────────────────┤
│  ✓ 12 passed  ✗ 1 failed  ● 1 skipped  │  Total: 14  │  0.8s       │
│  [R]un All  [F]ilter  [C]lear  [Q]uit                               │
└──────────────────────────────────────────────────────────────────────┘
```

### Status Indicators
| Icon | Meaning |
|------|---------|
| `✓` | Passed (green) |
| `✗` | Failed (red) |
| `●` | Skipped/Pending (yellow) |
| `◌` | Not yet run (dim) |
| `⟳` | Running (cyan, animated) |

### Hotkeys
| Key | Action |
|-----|--------|
| `R` | Force run all tests |
| `F` | Open filter dialog |
| `C` | Clear results |
| `Q` / `Ctrl+C` | Quit |
| `↑↓` | Navigate test tree |
| `Enter` | Expand/collapse tree node |
| `Tab` | Switch focus between panels |

---

## 5. Core Data Models

```csharp
// === Enums ===

public enum PistonPhase
{
    Idle,
    Watching,
    Building,
    Testing,
    Error
}

public enum TestStatus
{
    NotRun,
    Running,
    Passed,
    Failed,
    Skipped
}

public enum BuildStatus
{
    None,
    Building,
    Succeeded,
    Failed
}

// === Core Models ===

public sealed record TestResult(
    string FullyQualifiedName,      // e.g. "MyProject.Tests.MathTests.AddNumbers"
    string DisplayName,
    TestStatus Status,
    TimeSpan Duration,
    string? Output,
    string? ErrorMessage,
    string? StackTrace,
    string? Source                    // file path
);

public sealed record TestSuite(
    string Name,                     // assembly/project name
    IReadOnlyList<TestResult> Tests,
    DateTimeOffset Timestamp,
    TimeSpan TotalDuration
);

public sealed record BuildResult(
    BuildStatus Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration
);

public sealed record FileChangeEvent(
    string FilePath,
    WatcherChangeTypes ChangeType,
    DateTimeOffset Timestamp
);

// === State ===

public sealed class PistonState
{
    public PistonPhase Phase { get; set; }
    public string? SolutionPath { get; set; }
    public BuildResult? LastBuild { get; set; }
    public IReadOnlyList<TestSuite> TestSuites { get; set; } = [];
    public DateTimeOffset? LastRunTime { get; set; }
    public int TotalPassed => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Passed);
    public int TotalFailed => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Failed);
    public int TotalSkipped => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Skipped);
    public event Action? StateChanged;
    public void NotifyChanged() => StateChanged?.Invoke();
}
```

---

## 6. Component Design

### 6.1 FileWatcherService
- Wraps `FileSystemWatcher` for `.cs`, `.csproj`, `.props`, `.targets` files
- Watches solution directory recursively
- Excludes `bin/`, `obj/`, `.git/`, `node_modules/`
- Fires `FileChanged` event with debouncing (configurable, default 300ms)
- Uses a `System.Threading.Timer` for debounce

```csharp
public interface IFileWatcherService : IDisposable
{
    event Action<FileChangeEvent> FileChanged;
    void Start(string solutionDirectory);
    void Stop();
}
```

### 6.2 BuildService
- Runs `dotnet build <solution> --no-restore` via `Process`
- Captures stdout/stderr line-by-line
- Parses MSBuild output for errors/warnings
- Supports cancellation (kills process on cancel)
- Returns `BuildResult`

```csharp
public interface IBuildService
{
    Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct);
}
```

### 6.3 TestRunnerService
- Runs `dotnet test <solution> --no-build --logger "trx;LogFileName=piston-results.trx" --results-directory <temp>`
- Finds and parses TRX file(s) after completion
- Supports cancellation
- Returns list of `TestSuite`

```csharp
public interface ITestRunnerService
{
    Task<IReadOnlyList<TestSuite>> RunTestsAsync(string solutionPath, CancellationToken ct);
}
```

### 6.4 TrxResultParser
- Parses TRX XML files into `TestSuite`/`TestResult` models
- Handles multiple TRX files (one per test project)
- Extracts: test name, status, duration, error message, stack trace, stdout

```csharp
public interface ITestResultParser
{
    IReadOnlyList<TestSuite> Parse(string trxFilePath);
}
```

### 6.5 PistonOrchestrator
- The pipeline coordinator
- Subscribes to `FileWatcherService.FileChanged`
- On change: cancel current run → build → test → update state
- Manages `CancellationTokenSource` lifecycle
- Updates `PistonState` at each phase transition

```csharp
public interface IPistonOrchestrator : IDisposable
{
    Task StartAsync(string solutionPath);
    Task ForceRunAsync();
    void Stop();
}
```

### 6.6 MainView (TUI)
- SharpConsoleUI `ConsoleWindowSystem` with a single maximized `Window`:
  - `HorizontalGridControl` for left/right split layout
  - Left column: `TreeControl` for test hierarchy (namespace → class → method)
  - Right column: `ScrollablePanelControl` with `MarkupControl` for test detail (output, error, stack trace)
  - `StickyTop` `HorizontalGridControl` for header bar (phase indicator, solution name, last run time)
  - `StickyBottom` `MarkupControl` for status bar (pass/fail/skip counts + hotkey hints)
- Uses `WithAsyncWindowThread` for background state polling / UI updates
- Window-level `KeyPressed` handler for hotkeys (R, F, C, Q)
- Named controls (`FindControl<T>("name")`) for targeted updates from background thread
- Spectre.Console markup for rich formatting: `[green]✓[/]`, `[red]✗[/]`, `[bold]...[/]`

```csharp
// Initialization sketch
var driver = new NetConsoleDriver(RenderMode.Buffer);
var windowSystem = new ConsoleWindowSystem(driver);

var mainWindow = new WindowBuilder(windowSystem)
    .WithTitle("Piston")
    .Borderless()
    .Maximized()
    .WithAsyncWindowThread(UpdateLoopAsync)
    .OnKeyPressed(HandleKeyPress)
    .Build();

// Header bar (sticky top)
var headerBar = Controls.HorizontalGrid()
    .StickyTop()
    .Column(col => col.Add(Controls.Markup("[green]● WATCHING[/]").WithName("phase").Build()))
    .Column(col => col.Add(Controls.Markup("MyProject.sln").WithName("solution").Build()))
    .Column(col => col.Add(Controls.Markup("Last run: --:--:--").WithName("lastrun").Build()))
    .Build();

// Main split: test tree (left) + detail (right)
var testTree = Controls.Tree().WithName("testtree").Build();

var detailContent = Controls.Markup("Select a test to view details.")
    .WithName("detail-content")
    .Build();
var detailPanel = Controls.ScrollablePanel()
    .WithName("detail")
    .WithVerticalAlignment(VerticalAlignment.Fill)
    .Build();
detailPanel.AddControl(detailContent);

var mainGrid = Controls.HorizontalGrid()
    .WithVerticalAlignment(VerticalAlignment.Fill)
    .Column(col => col.Width(30).Add(testTree))
    .Column(col => col.Width(1)) // spacer
    .Column(col => col.Add(detailPanel))
    .Build();

// Status bar (sticky bottom)
var statusBar = Controls.Markup("[green]✓ 0[/]  [red]✗ 0[/]  [yellow]● 0[/]  │  [grey]R[/]un  [grey]F[/]ilter  [grey]C[/]lear  [grey]Q[/]uit")
    .WithName("statusbar")
    .StickyBottom()
    .Build();

mainWindow.AddControl(headerBar);
mainWindow.AddControl(mainGrid);
mainWindow.AddControl(statusBar);
windowSystem.AddWindow(mainWindow);
windowSystem.Run();
```

---

## 7. Project Structure

```
piston/
├── .gitignore
├── global.json                          # Pin .NET 10 SDK
├── Directory.Build.props                # Shared build properties
├── Piston.sln
│
├── src/
│   ├── Piston/                          # TUI application (executable)
│   │   ├── Piston.csproj
│   │   ├── Program.cs                   # Entry point, DI setup, SharpConsoleUI init
│   │   ├── Views/
│   │   │   ├── PistonWindow.cs          # Main window — layout, controls, keyboard
│   │   │   ├── TestTreeBuilder.cs       # Builds TreeControl nodes from TestSuites
│   │   │   ├── TestDetailRenderer.cs    # Renders selected test into detail panel
│   │   │   └── StatusBarRenderer.cs     # Renders pass/fail/skip counts + phase
│   │   └── ViewModels/
│   │       └── TestNode.cs              # Tree node model for SharpConsoleUI TreeControl
│   │
│   └── Piston.Core/                     # Core services (no TUI dependency)
│       ├── Piston.Core.csproj
│       ├── Models/
│       │   ├── TestResult.cs
│       │   ├── TestSuite.cs
│       │   ├── BuildResult.cs
│       │   ├── FileChangeEvent.cs
│       │   └── PistonState.cs
│       ├── Services/
│       │   ├── IFileWatcherService.cs
│       │   ├── FileWatcherService.cs
│       │   ├── IBuildService.cs
│       │   ├── BuildService.cs
│       │   ├── ITestRunnerService.cs
│       │   ├── TestRunnerService.cs
│       │   ├── ITestResultParser.cs
│       │   └── TrxResultParser.cs
│       └── Orchestration/
│           ├── IPistonOrchestrator.cs
│           └── PistonOrchestrator.cs
│
└── tests/
    └── Piston.Core.Tests/               # Unit tests for core services
        ├── Piston.Core.Tests.csproj
        ├── Services/
        │   ├── FileWatcherServiceTests.cs
        │   ├── BuildServiceTests.cs
        │   ├── TestRunnerServiceTests.cs
        │   └── TrxResultParserTests.cs
        └── Orchestration/
            └── PistonOrchestratorTests.cs
```

---

## 8. Implementation Plan

### Phase 1: Project Scaffolding & Basic TUI Shell
- [ ] Create `.gitignore` for .NET
- [ ] Create `global.json` pinning .NET 10 SDK
- [ ] Create `Directory.Build.props` with shared settings (nullable, implicit usings, etc.)
- [ ] Create `Piston.sln`
- [ ] Create `src/Piston/Piston.csproj` (exe, net10.0, SharpConsoleUI package ref)
- [ ] Create `src/Piston.Core/Piston.Core.csproj` (classlib, net10.0)
- [ ] Create `tests/Piston.Core.Tests/Piston.Core.Tests.csproj` (xUnit)
- [ ] Add project references (Piston → Piston.Core, Tests → Piston.Core)
- [ ] Create `Program.cs` with SharpConsoleUI initialization (`ConsoleWindowSystem`, `NetConsoleDriver(RenderMode.Buffer)`)
- [ ] Create `PistonWindow.cs` with maximized borderless window, header bar (StickyTop), main HorizontalGridControl (tree + detail split), status bar (StickyBottom)
- [ ] Verify `dotnet build` and `dotnet run` work with basic TUI shell

### Phase 2: File Watching & Build Integration
- [ ] Create core models: `FileChangeEvent`, `BuildResult`, `BuildStatus` enum, `PistonPhase` enum
- [ ] Create `PistonState` with `StateChanged` event
- [ ] Implement `IFileWatcherService` / `FileWatcherService` with debouncing
- [ ] Implement `IBuildService` / `BuildService` (process wrapper for `dotnet build`)
- [ ] Write unit tests for debounce logic
- [ ] Write unit tests for build output parsing
- [ ] Integrate file watcher → build pipeline in orchestrator skeleton
- [ ] Update TUI to show phase transitions (Watching → Building → result)

### Phase 3: Test Discovery & Execution
- [ ] Create test models: `TestResult`, `TestSuite`, `TestStatus` enum
- [ ] Implement `ITestResultParser` / `TrxResultParser` (parse TRX XML)
- [ ] Implement `ITestRunnerService` / `TestRunnerService` (process wrapper for `dotnet test`)
- [ ] Write unit tests for TRX parsing (with sample TRX files)
- [ ] Write unit tests for test runner service
- [ ] Complete `PistonOrchestrator`: FileWatch → Build → Test → State update
- [ ] Add cancellation support (new changes cancel in-progress run)

### Phase 4: Full TUI with Test Tree & Detail Panels
- [ ] Implement `TestNode` tree model (namespace → class → method hierarchy)
- [ ] Implement `TestTreeBuilder` — populates SharpConsoleUI `TreeControl` from `TestSuites` with pass/fail icons via Spectre markup (`[green]✓[/]`, `[red]✗[/]`)
- [ ] Implement `TestDetailRenderer` — renders selected test's output, error, stack trace, duration into `ScrollablePanelControl` with `MarkupControl`
- [ ] Implement `StatusBarRenderer` — updates sticky-bottom `MarkupControl` with pass/fail/skip counts, last run time, hotkey hints
- [ ] Wire up TreeControl `SelectedNodeChanged` → detail panel updates
- [ ] Add window-level `KeyPressed` handler for hotkeys: R (run all), F (filter), C (clear), Q (quit)
- [ ] Add phase indicator in header bar with Spectre colors: `[green]● WATCHING[/]`, `[yellow]⟳ BUILDING[/]`, `[cyan]⟳ TESTING[/]`, `[red]✗ ERROR[/]`
- [ ] Implement `WithAsyncWindowThread` update loop to poll `PistonState` and refresh controls

### Phase 5: Polish & Configuration
- [ ] Add CLI argument parsing (solution path, verbosity, debounce time)
- [ ] Add optional `.piston.json` config file support
- [ ] Add test name filtering (search/regex)
- [ ] Add error handling for missing SDK, bad solution path, etc.
- [ ] Add graceful shutdown (cleanup watchers, kill processes)
- [ ] Add `--version` and `--help` flags
- [ ] Performance: only re-run affected test projects (based on changed files)
- [ ] Add color theme support (light/dark)
- [ ] README.md with usage instructions and screenshots

---

## 9. Key NuGet Packages

| Package | Version | Project | Purpose |
|---------|---------|---------|---------|
| `SharpConsoleUI` | 2.x | Piston | TUI windowing framework (built on Spectre.Console) |
| `Spectre.Console` | 0.54+ | Piston | Rich console rendering (pulled in by SharpConsoleUI) |
| `Microsoft.Extensions.DependencyInjection` | 10.x | Piston | DI container |
| `Microsoft.Extensions.Logging` | 10.x | Piston.Core | Logging abstraction |
| `Microsoft.Extensions.Logging.File` or `Serilog.Sinks.File` | latest | Piston | File logging (can't log to console in TUI) |
| `Microsoft.Extensions.Configuration` | 10.x | Piston | Config file support |
| `Microsoft.Extensions.Configuration.Json` | 10.x | Piston | JSON config provider |
| `System.CommandLine` | latest | Piston | CLI argument parsing |
| `xunit` | 2.x | Tests | Test framework |
| `xunit.runner.visualstudio` | 2.x | Tests | Test runner |
| `Microsoft.NET.Test.Sdk` | 17.x | Tests | Test SDK |

---

## 10. Future Considerations

These are explicitly **out of scope** for v1 but worth thinking about:

- **Code coverage integration** — Run with `coverlet`, overlay coverage data on test tree
- **Git-aware test selection** — Only run tests affected by uncommitted changes (via dependency graph)
- **VS Code extension** — Surface Piston results in VS Code sidebar
- **Distributed/parallel testing** — Split test execution across cores/machines
- **Watch expressions** — Show live variable values from last test run
- **Notification integration** — Desktop notifications on test failure
- **Performance trending** — Track test duration over time, flag regressions
- **dotnet-tool packaging** — Publish as `dotnet tool install -g piston`
- **AOT compilation** — Publish as single native binary for fast startup
- **Multi-solution support** — Watch multiple solutions simultaneously
- **Custom test commands** — Support non-dotnet test runners (jest, cargo test, etc.)

---

## Appendix: TRX Format Reference

TRX files are XML with this key structure:
```xml
<TestRun>
  <Results>
    <UnitTestResult testName="AddNumbers" outcome="Passed" duration="00:00:00.023">
      <Output>
        <StdOut>...</StdOut>
        <ErrorInfo>
          <Message>Assert.Equal() Failure</Message>
          <StackTrace>at MathTests.cs:line 42</StackTrace>
        </ErrorInfo>
      </Output>
    </UnitTestResult>
  </Results>
  <TestDefinitions>
    <UnitTest name="AddNumbers">
      <TestMethod className="MathTests" name="AddNumbers" />
    </UnitTest>
  </TestDefinitions>
</TestRun>
```

Key XPaths:
- `//UnitTestResult` — individual test outcomes
- `//UnitTestResult/@outcome` — Passed, Failed, NotExecuted
- `//UnitTestResult/Output/ErrorInfo/Message` — failure message
- `//UnitTestResult/Output/ErrorInfo/StackTrace` — stack trace
- `//TestMethod/@className` — for building the tree hierarchy
