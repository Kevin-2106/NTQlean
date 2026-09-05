@echo off
rem NTQlean GUI launcher (double-click friendly)
cd /d "%~dp0"
if not exist "src\NTQlean.App\bin\Debug\net8.0-windows\NTQlean.App.exe" (
    echo First run: building NTQlean.App ...
    dotnet build -v q
)
start "" "src\NTQlean.App\bin\Debug\net8.0-windows\NTQlean.App.exe"
