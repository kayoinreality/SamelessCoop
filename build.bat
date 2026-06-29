@echo off
REM Compila o SamelessCoop.exe usando o compilador C# embutido no Windows.
REM Nao precisa instalar nada.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

echo Compilando SamelessCoop.exe ...
"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0SamelessCoop.exe" "%~dp0launcher\SamelessCoopLauncher.cs"
if errorlevel 1 (
  echo.
  echo FALHA na compilacao.
  pause
  exit /b 1
)
echo.
echo OK: SamelessCoop.exe gerado.
pause
