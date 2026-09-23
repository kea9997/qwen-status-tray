@echo off
chcp 65001>nul
if exist "%~dp0..\QwenWork" cd /d "%~dp0..\QwenWork"
if not exist "%LOCALAPPDATA%\hermes\node\hermes.cmd" (
  echo Hermes launcher not found.
  exit /b 1
)
call "%LOCALAPPDATA%\hermes\node\hermes.cmd" %*
