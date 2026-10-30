@echo off
rem Weight Studio + Weight Studio Reader installer launcher
rem Looks for your VNyan folder (or asks you to open it); asks for admin rights ONLY if that folder needs them.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
