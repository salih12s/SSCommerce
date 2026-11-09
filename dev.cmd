@echo off
rem Yerel gelistirme: API + web tek komutla. Ayrinti: dev.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dev.ps1" %*
