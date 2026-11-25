@rem Runs Piston from this staged copy (see piston.cmd in the tool package).
@dotnet "%~dp0Piston.dll" %*
@exit /b %errorlevel%
