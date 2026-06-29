@echo off
REM Compila o ds2_seamless_coop.dll (e dinput8.dll) a partir do fonte,
REM usando o MSVC + CMake + Ninja que ja vem no VS 2022 Build Tools.
setlocal
set "VS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
set "CMAKE=%VS%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "NINJA=%VS%\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
cd /d "%~dp0"

echo === Configurando (CMake/Ninja) ===
"%CMAKE%" -G Ninja -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_LAUNCHER=OFF -DCMAKE_MAKE_PROGRAM="%NINJA%"
if errorlevel 1 ( echo FALHA no configure & exit /b 1 )

echo === Compilando ===
"%CMAKE%" --build build
if errorlevel 1 ( echo FALHA no build & exit /b 1 )

echo === Procurando o .dll gerado ===
dir /s /b build\*.dll
echo BUILD_OK
endlocal
