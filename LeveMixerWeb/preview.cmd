@echo off
rem =========================================================
rem  Preview do Volum: ve suas mudancas na sua maquina, sem publicar nada.
rem  1. fecha o Volum que estiver aberto (instalado ou de teste)
rem  2. compila o projeto
rem  3. abre o Volum de teste (modo --dev): salvou o CSS/JS/HTML, ele recarrega sozinho
rem  Para voltar ao Volum normal: feche o de teste (Sair) e abra "Volum" no menu Iniciar.
rem =========================================================
cd /d "%~dp0"
taskkill /im Volum.exe /f >nul 2>&1
echo Compilando...
dotnet build -c Release -v quiet -nologo
if errorlevel 1 (
  echo.
  echo Deu erro na compilacao. Veja a mensagem acima.
  pause
  exit /b 1
)
start "" "bin\Release\net8.0-windows\Volum.exe" --dev
echo Preview aberto!
