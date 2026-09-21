@echo off
title new-acad port config
cd /d %~dp0..
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0port-config.ps1"
