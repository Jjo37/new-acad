@echo off
title new-acad port check
cd /d %~dp0..
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0port-check.ps1"
echo.
pause
