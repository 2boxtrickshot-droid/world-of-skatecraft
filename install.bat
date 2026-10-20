@echo off
rem World of Skatecraft one-click installer (Windows). Installs prerequisites, then runs setup.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\install-windows.ps1" %*
pause
