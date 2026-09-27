@echo off
setlocal

:: ============================================================
:: NARC AGENT v3 - BUILD SCRIPT
:: ============================================================

:menu
cls
echo ================================================
echo     NARC AGENT v3 - BUILD TOOL
echo ================================================
echo.
echo Build disponiveis:
echo   [1] Windows  (win-x64)
echo   [2] Linux    (linux-x64)
echo   [3] Linux ARM (linux-arm64)
echo   [4] Todas as plataformas
echo   [5] Limpar pasta de build
echo   [6] Sair

set /p choice=Digite sua opcao (1-6): 

goto case_%choice%

:case_1
call :build win-x64
pause
goto menu

:case_2
call :build linux-x64
pause
goto menu

:case_3
call :build linux-arm64
pause
goto menu

:case_4
call :build win-x64
call :build linux-x64
call :build linux-arm64
pause
goto menu

:case_5
echo.
if exist build (
    rmdir /s /q build
    echo Pasta build/ removida.
) else (
    echo Nada a limpar.
)
pause
goto menu

:case_6
exit /b 0

:default
echo Opcao invalida.
pause
goto menu

:: ============================================================
:: SUB-ROTINA DE BUILD
:: ============================================================
:build
set "rid=%~1"
set "output=build\%rid%"

cls
echo ================================================
echo     COMPILANDO: %rid%
echo ================================================
echo.

echo [1/4] Limpando diretorio anterior...
if exist "%output%" rmdir /s /q "%output%" 2>nul

echo [2/4] Executando dotnet publish do agente base...
dotnet publish "NarcAgent\NarcAgent.csproj" -c Release -r %rid% --self-contained false -p:PublishSingleFile=true -o "%output%"

if %errorlevel% neq 0 (
    echo.
    echo [ERRO] Build falhou para %rid% no agente base.
    pause
    exit /b 1
)

echo [3/4] Executando dotnet publish da ferramenta CLI (NarcAgent.Cli)...
dotnet publish "NarcAgent.Cli\NarcAgent.Cli.csproj" -c Release -r %rid% --self-contained false -p:PublishSingleFile=true -o "%output%"

if %errorlevel% neq 0 (
    echo.
    echo [ERRO] Build falhou para %rid% no CLI.
    pause
    exit /b 1
)

echo [4/4] Compilando plugin NarcAgent.Proxy...
dotnet publish "NarcAgent.Proxy\NarcAgent.Proxy.csproj" -c Release -r %rid% -o "%output%\plugins"

if %errorlevel% neq 0 (
    echo.
    echo [ERRO] Build falhou para %rid% no plugin Proxy.
    pause
    exit /b 1
)

echo.
echo [SUCESSO] Build %rid% concluido!
echo    Pasta: %output%
exit /b 0