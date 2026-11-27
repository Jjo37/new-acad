@echo off
title new-acad host manager (add / remove single version)
cd /d %~dp0
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\manage-hosts.ps1" %*
echo.
pause
