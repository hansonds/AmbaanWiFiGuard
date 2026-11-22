@echo off
title Wi-Fi Network Analyzer Pro V5
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0WiFiNetworkAnalyzerPro.ps1"
