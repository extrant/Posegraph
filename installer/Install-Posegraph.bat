@echo off
rem Posegraph backend installer - double-click to run.
rem Runs Install-Posegraph.ps1 with -ExecutionPolicy Bypass for this run only; no system or user settings are changed.
title Posegraph backend installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Posegraph.ps1" %*
