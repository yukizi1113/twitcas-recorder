@echo off
setlocal
chcp 65001 > nul

echo ツイキャス録音君をビルド中...

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo C# コンパイラが見つかりません: %CSC%
  exit /b 1
)

set "SRC="
set "OUT="
for %%F in (*.cs) do (
  set "SRC=%%~fF"
  set "OUT=%%~dpnF.exe"
  goto found
)

echo .cs ファイルが見つかりません。
exit /b 1

:found
"%CSC%" ^
  /target:winexe ^
  /out:"%OUT%" ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Web.Extensions.dll ^
  /reference:System.Net.dll ^
  /utf8output ^
  /optimize+ ^
  "%SRC%"

if errorlevel 1 (
  echo.
  echo ビルド失敗。上記エラーを確認してください。
  exit /b 1
)

echo.
echo ビルド成功: "%OUT%"
endlocal
