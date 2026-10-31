@echo off
setlocal
set ROOT=%~dp0..
set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release

if not defined VS100COMNTOOLS (
  echo VS100COMNTOOLS is not set. Install Visual Studio 2010.
  exit /b 1
)
call "%VS100COMNTOOLS%\..\..\VC\vcvarsall.bat" x86 >nul
if errorlevel 1 exit /b 1

if not exist "%ROOT%\build\logs" mkdir "%ROOT%\build\logs"
set MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe
echo Building Cuphead360 Win32 %CONFIG%...
"%MSBUILD%" "%ROOT%\projects\win32\Cuphead360.vcxproj" /nologo /t:Rebuild /p:Configuration=%CONFIG% /p:Platform=Win32 /v:minimal /fl "/flp:logfile=%ROOT%\build\logs\win32_%CONFIG%.log;verbosity:normal"
set RESULT=%ERRORLEVEL%
if not "%RESULT%"=="0" (
  echo Win32 build FAILED. See build\logs\win32_%CONFIG%.log
  exit /b %RESULT%
)
echo Win32 build OK: %ROOT%\build\win32\Cuphead360.exe
exit /b 0
