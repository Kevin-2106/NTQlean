@echo off
rem NTQlean GUI launcher (double-click friendly).
rem Works in two layouts: release zip (NTQlean.App.exe next to this file)
rem and source repo (builds on first run).
rem Unofficial community tool, not affiliated with Tencent.
rem Use only on your own machine and account, at your own risk (see README).
setlocal
cd /d "%~dp0"

set "APP=NTQlean.App.exe"
if not exist "%APP%" set "APP=src\NTQlean.App\bin\Debug\net8.0-windows\NTQlean.App.exe"

if not exist "%APP%" (
    echo First run: building NTQlean.App ...
    dotnet build -v q
)

if not exist "%APP%" (
    echo Build failed or NTQlean.App.exe not found. See README.md for manual build steps.
    pause
    exit /b 1
)

start "" "%APP%"
