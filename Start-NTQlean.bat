@echo off
rem NTQlean GUI launcher (double-click friendly)
rem Unofficial community tool, not affiliated with Tencent.
rem Use only on your own machine and account, at your own risk (see README: 免责声明).
cd /d "%~dp0"
if not exist "src\NTQlean.App\bin\Debug\net8.0-windows\NTQlean.App.exe" (
    echo First run: building NTQlean.App ...
    dotnet build -v q
)
start "" "src\NTQlean.App\bin\Debug\net8.0-windows\NTQlean.App.exe"
