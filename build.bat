@echo off
rem Builds VideoCutter.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem No SDK, no downloads. Just double-click this file.
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WPF=%FW%\WPF
if not exist "%FW%\csc.exe" (
  echo Could not find %FW%\csc.exe - .NET Framework 4.x is required.
  pause
  exit /b 1
)
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu /out:VideoCutter.exe ^
  /win32icon:src\app.ico ^
  /resource:src\MainWindow.xaml,MainWindow.xaml ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Web.Extensions.dll" /r:System.Core.dll /r:System.dll ^
  src\App.cs src\Engine.cs src\Timeline.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  if "%1"=="" pause
  exit /b 1
)
echo Built VideoCutter.exe
if "%1"=="" pause
