using Piston.Core;
using Piston.Core.Orchestration;
using Piston.Core.Services;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Piston.Views;

// --- Compose core services ---
var state = new PistonState();
var fileWatcher = new FileWatcherService();
var buildService = new BuildService();
var trxParser = new TrxResultParser();
var testRunner = new TestRunnerService(trxParser);
var orchestrator = new PistonOrchestrator(fileWatcher, buildService, testRunner, state);

// --- Start TUI ---
var driver = new NetConsoleDriver(RenderMode.Buffer);
var windowSystem = new ConsoleWindowSystem(driver);

PistonWindow.Create(windowSystem, state, orchestrator);

windowSystem.Run();

// --- Cleanup ---
orchestrator.Dispose();
