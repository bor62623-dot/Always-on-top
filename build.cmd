@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] C# compiler not found. .NET Framework 4.x is required.
    exit /b 1
)

"%CSC%" -nologo -target:winexe -out:AlwaysOnTop.exe ^
    -r:System.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll ^
    src\AlwaysOnTop.cs

if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo [OK] Built: AlwaysOnTop.exe
