@echo off
rem Windows entry point of the Piston tool. Piston runs from a per-build copy under
rem %LOCALAPPDATA%\Piston\runtimes, so the .NET tool store holds no open files and
rem `dotnet tool update` can replace this version while Piston processes keep running.
rem The final command hands control to the copy without returning to this file, which an
rem update may delete.
setlocal
set "PISTON_RUNTIME_ID="
set /p PISTON_RUNTIME_ID=<"%~dp0piston.runtime-id"
if not defined PISTON_RUNTIME_ID goto missing
set "PISTON_RUNTIMES=%LOCALAPPDATA%"
if not defined PISTON_RUNTIMES set "PISTON_RUNTIMES=%TEMP%"
set "PISTON_RUNTIME=%PISTON_RUNTIMES%\Piston\runtimes\%PISTON_RUNTIME_ID%"
if exist "%PISTON_RUNTIME%\.piston-runtime" goto run
dotnet "%~dp0Piston.dll" __stage-runtime "%~dp0." "%PISTON_RUNTIME%"
if errorlevel 1 exit /b 1
:run
(type nul > "%PISTON_RUNTIME%\.last-used") 2>nul
endlocal & "%PISTON_RUNTIME%\piston-run.cmd" %*
:missing
echo piston: "%~dp0piston.runtime-id" is missing; reinstall the tool. 1>&2
exit /b 1
