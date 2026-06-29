@echo off
setlocal enabledelayedexpansion
title SamelessCoop - Instalador
cd /d "%~dp0"
color 0A

echo.
echo  ================= SAMELESSCOOP - INSTALADOR =================
echo   Dark Souls II: Seamless Co-op (launcher unico + save separado)
echo  ============================================================
echo.

REM ---------- 1) .NET Framework (necessario para o launcher) ----------
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo  [ERRO] .NET Framework 4 nao encontrado. Instale o .NET Framework 4.8:
  echo         https://dotnet.microsoft.com/download/dotnet-framework
  pause & exit /b 1
)
echo  [OK] .NET Framework encontrado.

REM ---------- 2) Visual C++ Redistributable (runtime do .dll) ----------
if exist "%WINDIR%\System32\vcruntime140.dll" if exist "%WINDIR%\System32\msvcp140.dll" (
  echo  [OK] Visual C++ Redistributable presente.
  goto vc_done
)
echo  [..] Visual C++ Redistributable ausente. Baixando da Microsoft...
powershell -NoProfile -Command "try { Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile 'vc_redist.x64.exe' } catch { exit 1 }"
if errorlevel 1 (
  echo  [!] Nao consegui baixar. Instale manualmente: https://aka.ms/vs/17/release/vc_redist.x64.exe
) else (
  echo  [..] Instalando Visual C++ Redistributable...
  vc_redist.x64.exe /install /quiet /norestart
  del /q vc_redist.x64.exe >nul 2>&1
  echo  [OK] Visual C++ Redistributable instalado.
)
:vc_done

REM ---------- 3) Arquivos do mod (pasta unica mod\) ----------
if exist "mod\dinput8.dll" if exist "mod\Server\Server.exe" (
  echo  [OK] Arquivos do mod presentes na pasta mod.
  goto mod_done
)
echo  [!] mod\dinput8.dll ausente. Tentando compilar do codigo-fonte...
if exist "src\build_dll.bat" (
  call "src\build_dll.bat"
  if exist "src\build\bin\Release\dinput8.dll" (
    if not exist "mod" mkdir mod
    copy /y "src\build\bin\Release\dinput8.dll" "mod\dinput8.dll" >nul
    if exist "src\dist\host\ds2_server_public.key" copy /y "src\dist\host\ds2_server_public.key" "mod\" >nul
    if not exist "mod\Server" if exist "src\Release\Server" xcopy /e /i /y "src\Release\Server" "mod\Server" >nul
    echo  [OK] dll compilada e mod\ montada.
  ) else (
    echo  [ERRO] Falha ao compilar o dll. Use uma copia completa do projeto.
    pause & exit /b 1
  )
) else (
  echo  [ERRO] Arquivos do mod ausentes e sem codigo-fonte para compilar.
  pause & exit /b 1
)
:mod_done

REM ---------- 4) Compilar o launcher ----------
echo  [..] Compilando SamelessCoop.exe...
"%CSC%" /nologo /target:exe /platform:x64 /out:"%~dp0SamelessCoop.exe" "%~dp0launcher\SamelessCoopLauncher.cs"
if errorlevel 1 ( echo  [ERRO] Falha ao compilar o launcher. & pause & exit /b 1 )
echo  [OK] SamelessCoop.exe compilado.

REM ---------- 5) Autoteste de seguranca do save ----------
echo  [..] Rodando autoteste de seguranca do save (sandbox)...
"%~dp0SamelessCoop.exe" --self-test
if errorlevel 1 ( echo  [ERRO] Autoteste falhou. NAO use ainda. & pause & exit /b 1 )

echo.
echo  ================= INSTALACAO CONCLUIDA =================
echo   Para jogar: rode  SamelessCoop.exe
echo   Ele abre a CONFIGURACAO (modo, IP, senha, dificuldade) e
echo   depois inicia o jogo. SEM menu dentro do jogo.
echo  =======================================================
echo.
set /p RUN="  Abrir o launcher agora? (s/N): "
if /i "%RUN%"=="s" start "" "%~dp0SamelessCoop.exe"
endlocal
