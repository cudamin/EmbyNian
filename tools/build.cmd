@echo off
rem ---------------------------------------------------------------------------
rem  Build / restore wrapper for this machine.
rem
rem  Why this exists: this session runs inside a sandbox that strips several
rem  Windows environment variables from the process environment of every child
rem  process. NuGet reads them directly (it does not use Environment.GetFolderPath),
rem  so the moment NuGet's ConfigurationDefaults static initialiser runs it gets a
rem  null path and throws:
rem
rem      error : Value cannot be null. (Parameter 'path1')
rem          at NuGet.Common.NuGetEnvironment.CalculateFolderPath(...)
rem          at NuGet.Configuration.XPlatMachineWideSetting..ctor()
rem
rem  That kills restore, and a killed restore leaves project.assets.json without
rem  its "frameworks" section - which then makes the build fail with NETSDK1060
rem  while reading that same file. Restoring the variables below before dotnet
rem  starts is the whole fix.
rem
rem  The one that actually bit: PROGRAMFILES(X86). NuGet's Windows branch of
rem  MachineWideSettingsBaseDirectory reads exactly that name (parentheses and
rem  all) and falls back to PROGRAMFILES only when it is empty. Then
rem  MachineWideConfigDirectory = MachineWideSettingsBaseDirectory + "\Config",
rem  and Path.Combine(null, "Config") is the ArgumentNullException. The rest of
rem  the list is here because the same code path and the same sandbox touch them.
rem
rem  Note the variable name contains parentheses, so the `set` name must be
rem  quoted: set "ProgramFiles(x86)=...".
rem
rem  Keep this file pure ASCII. A UTF-8 Chinese comment here is read as mojibake
rem  by the batch parser and the line after it turns into a bogus command, which
rem  silently skips the rest of the script.
rem
rem  Usage:  tools\build.cmd restore
rem          tools\build.cmd build
rem          tools\build.cmd test
rem          tools\build.cmd publish
rem ---------------------------------------------------------------------------

call :env

set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
set "REPO=%~dp0.."

rem Plain exit /b preserves the child exit code; percent expansion happens before a block runs.
if /I "%~1"=="restore" (
    "%DOTNET%" restore "%REPO%\EmbyNian.sln"
    exit /b
)

if /I "%~1"=="build" (
    "%DOTNET%" build "%REPO%\EmbyNian.sln" -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:MSBuildNodeReuse=false
    exit /b
)

if /I "%~1"=="test" (
    "%DOTNET%" run --project "%REPO%\tests\EmbyNian.Tests\EmbyNian.Tests.csproj" -c Release --no-build
    exit /b
)

if /I "%~1"=="publish" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO%\tools\publish.ps1" -NoArchive
    exit /b
)

"%DOTNET%" %*
exit /b %ERRORLEVEL%

:env
set "ProgramFiles(x86)=C:\Program Files (x86)"
set "ProgramFiles=C:\Program Files"
set "ProgramW6432=C:\Program Files"
set "APPDATA=%USERPROFILE%\AppData\Roaming"
set "LOCALAPPDATA=%USERPROFILE%\AppData\Local"
set "ProgramData=%SystemDrive%\ProgramData"
set "ALLUSERSPROFILE=%SystemDrive%\ProgramData"
set "HOMEDRIVE=%SystemDrive%"
set "HOMEPATH=\Users\%USERNAME%"
set "TEMP=%USERPROFILE%\AppData\Local\Temp"
set "TMP=%USERPROFILE%\AppData\Local\Temp"
exit /b 0
