@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4 compiler not found.
  exit /b 1
)
"%CSC%" /nologo /target:winexe "/out:%~dp0QwenStatus.new.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "%~dp0QwenStatus.cs" "%~dp0TokenTestWindow.cs" "%~dp0QwenInsights.cs" "%~dp0QwenOperations.cs" "%~dp0QwenTelemetry.cs" "%~dp0QwenExperience.cs" "%~dp0QwenChatWindow.cs" "%~dp0QwenDelegation.cs"
exit /b %ERRORLEVEL%
