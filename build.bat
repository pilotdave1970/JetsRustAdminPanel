@echo off
REM Builds JetsRustAdmin.exe with the C# compiler that ships with Windows (no Visual Studio / SDK needed).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:icon.ico /resource:items.txt,items.txt /out:JetsRustAdmin.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll JetsRustAdmin.cs
if errorlevel 1 (echo BUILD FAILED) else (echo Built JetsRustAdmin.exe)
pause
