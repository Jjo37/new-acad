@echo off
title new-acad installer
cd /d %~dp0
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
