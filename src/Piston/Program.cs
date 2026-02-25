using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using Piston.Views;

var driver = new NetConsoleDriver(RenderMode.Buffer);
var windowSystem = new ConsoleWindowSystem(driver);

PistonWindow.Create(windowSystem);

windowSystem.Run();
