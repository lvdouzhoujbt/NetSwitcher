@echo off
rem Build NetSwitcher with the C# compiler built into Windows (no SDK needed)
rem Usage: double-click build.cmd  or run it from src directory
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler not found. Windows 10/11 should have it.
  exit /b 1
)
rem Icon is optional: generate it with `python make_icon.py` if missing
set ICONOPT=
if exist app.ico set ICONOPT=/win32icon:app.ico
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu ^
 /out:..\out\NetSwitcher.exe ^
 /win32manifest:app.manifest ^
 %ICONOPT% ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll ^
 NetSwitcher.cs
if %errorlevel%==0 (
  echo Build OK: ..\out\NetSwitcher.exe
) else (
  echo Build FAILED.
  exit /b 1
)
