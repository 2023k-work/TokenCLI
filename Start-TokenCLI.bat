@echo off
chcp 65001 >nul
setlocal DisableDelayedExpansion
set "tokenCliExe=%~dp0TokenCLI.exe"
if exist "%tokenCliExe%" goto ready
set "tokenCliExe=%~dp0dist\0.1.2\win-x64\TokenCLI.exe"
if exist "%tokenCliExe%" goto ready
set "tokenCliExe=%~dp0dist\win-x64\TokenCLI.exe"
if not exist "%tokenCliExe%" goto missing

:ready
if /i "%~1"=="--check" goto check
if not "%~1"=="" goto usage

:menu
cls
echo TokenCLI 金鑰管理
echo ========================================
echo 1. 設定 OpenAI / ChatGPT Token
echo 2. 設定 Claude / Anthropic Token
echo 3. 查看是否已設定 Token
echo 4. 刪除 OpenAI Token
echo 5. 刪除 Claude Token
echo 6. 查看 CLI 使用說明
echo 7. OpenAI 免費網路診斷
echo 8. Claude 免費網路診斷
echo 0. 離開
echo.
echo 設定時輸入不會顯示，貼上 Token 後按 Enter。
echo Token 儲存在目前 Windows 帳號的 Credential Manager。
echo 此選單不會呼叫 AI API；網路診斷僅檢查 DNS / TCP / TLS。
echo.
choice /c 123456780 /n /m "請選擇 [1-8,0]："
if errorlevel 255 exit /b 1
if errorlevel 9 exit /b 0
if errorlevel 8 goto doctor_anthropic
if errorlevel 7 goto doctor_openai
if errorlevel 6 goto help
if errorlevel 5 goto delete_anthropic
if errorlevel 4 goto delete_openai
if errorlevel 3 goto status
if errorlevel 2 goto anthropic
if errorlevel 1 goto openai
exit /b 1

:openai
"%tokenCliExe%" credentials set openai
if errorlevel 1 goto failed
echo 已儲存，之後同一 Windows 帳號會自動取用。
pause
goto menu

:anthropic
"%tokenCliExe%" credentials set anthropic
if errorlevel 1 goto failed
echo 已儲存，之後同一 Windows 帳號會自動取用。
pause
goto menu

:status
"%tokenCliExe%" credentials status
if errorlevel 1 goto failed
echo true 表示已儲存，false 表示尚未設定；這不會測試 API 金鑰是否有效。
pause
goto menu

:delete_openai
choice /c YN /n /m "確認刪除 OpenAI Token？[Y/N]："
if errorlevel 2 goto menu
if not errorlevel 1 goto menu
"%tokenCliExe%" credentials delete openai
if errorlevel 1 goto failed
pause
goto menu

:delete_anthropic
choice /c YN /n /m "確認刪除 Claude Token？[Y/N]："
if errorlevel 2 goto menu
if not errorlevel 1 goto menu
"%tokenCliExe%" credentials delete anthropic
if errorlevel 1 goto failed
pause
goto menu

:help
"%tokenCliExe%" help
if errorlevel 1 goto failed
pause
goto menu

:doctor_openai
"%tokenCliExe%" doctor network openai
echo 診斷已結束；失敗時請保留結果供檢查，勿直接重送已停止的 API 操作。
pause
goto menu

:doctor_anthropic
"%tokenCliExe%" doctor network anthropic
echo 診斷已結束；失敗時請保留結果供檢查，勿直接重送已停止的 API 操作。
pause
goto menu

:failed
echo 操作未完成，請查看上方錯誤；設定金鑰需在一般互動終端執行。
pause
goto menu

:check
"%tokenCliExe%" version
exit /b %errorlevel%

:missing
echo 找不到 TokenCLI.exe，請將 BAT 放在 EXE 旁邊或完整專案根目錄。 1>&2
if /i "%~1"=="--check" exit /b 1
pause
exit /b 1

:usage
echo Usage: Start-TokenCLI.bat [--check] 1>&2
exit /b 2
