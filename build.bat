@echo off
chcp 65001 > nul
echo ツイキャス録音君 ビルド中...

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

"%CSC%" ^
  /target:winexe ^
  /out:"ツイキャス録音君.exe" ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Web.Extensions.dll ^
  /reference:System.Net.dll ^
  /utf8output ^
  /optimize+ ^
  "ツイキャス録音君.cs"

if %ERRORLEVEL% == 0 (
  echo.
  echo ビルド成功！ ツイキャス録音君.exe を作成しました。
) else (
  echo.
  echo ビルド失敗。上記エラーを確認してください。
)
pause
