@echo off
rem Atalho para dar duplo clique: roda publicar.ps1 e mantem a janela aberta no fim.
rem Parametros repassados: publicar.cmd -Fechar   |   publicar.cmd -SemTestes
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publicar.ps1" %*
set RESULTADO=%ERRORLEVEL%
echo.
pause
exit /b %RESULTADO%
