@echo off
setlocal
set "ROOT=%~dp0..\bin\rvc-runtime\RVC20260718Nvidia"
set "PATH=%ROOT%\runtime;%PATH%"
"%ROOT%\runtime\python.exe" -u "%~dp0server.py" "%ROOT%"
pause
