@echo off
setlocal
cd /d "%~dp0"

echo Step 1 of 3: building GamePowerPlan.exe
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:GamePowerPlan.exe /win32manifest:..\GamePowerPlan.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll ..\GamePowerPlan.cs
if errorlevel 1 goto fail

echo Step 2 of 3: finding makeappx.exe from the Windows SDK
set "MAKEAPPX="
for /f "delims=" %%i in ('dir /b /s "%ProgramFiles(x86)%\Windows Kits\10\bin\makeappx.exe" 2^>nul ^| findstr /i "\\x64\\"') do set "MAKEAPPX=%%i"
if not defined MAKEAPPX (
    echo Could not find makeappx.exe. Install the "Windows SDK" and run this again.
    goto fail
)
echo Using %MAKEAPPX%

echo Step 3 of 3: packing GamePowerPlan.msix
if exist staging rmdir /s /q staging
mkdir staging
copy /y GamePowerPlan.exe staging\ >nul
copy /y AppxManifest.xml staging\ >nul
xcopy /e /i /q Assets staging\Assets >nul
if exist GamePowerPlan.msix del GamePowerPlan.msix
"%MAKEAPPX%" pack /d staging /p GamePowerPlan.msix /o
if errorlevel 1 goto fail

echo.
echo Done. GamePowerPlan.msix is ready to upload to Partner Center.
echo To test it first on this PC (needs Developer Mode on in Windows Settings):
echo     powershell Add-AppxPackage -Register "%~dp0staging\AppxManifest.xml"
pause
exit /b 0

:fail
echo.
echo Build failed. See the message above.
pause
exit /b 1
