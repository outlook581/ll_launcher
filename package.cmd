@echo off
setlocal EnableExtensions DisableDelayedExpansion
pushd "%~dp0" || exit /b 1

set "project=ll_launcher\ll_launcher.csproj"
set "out=%CD%\artifacts"
set "stage=%out%\package-%RANDOM%-%RANDOM%"
set "result=1"
if not exist "%out%\" mkdir "%out%" || goto :done
mkdir "%stage%" || goto :done

dotnet msbuild "%project%" -nologo -getProperty:Version -getResultOutputFile:"%stage%\version.txt" || goto :failed
set "version="
set /p "version="<"%stage%\version.txt"
if not defined version goto :failed
set "archive=Qidian-%version%-win-x64.zip"

dotnet publish "%project%" -c Release -p:PublishProfile=Portable -o "%stage%\publish" || goto :failed
for %%F in (
    README.md LICENSE NOTICE Assets\Launcher.ico Assets\Skins.json Assets\Icons\LICENSE
    Runtime\Microsoft.Data.Sqlite.dll Runtime\e_sqlite3.dll
    Runtime\Licenses\SQLite-NOTICES.txt Runtime\Licenses\Microsoft.Data.Sqlite-LICENSE.txt Runtime\Licenses\Apache-2.0.txt
) do (
    if not exist "%stage%\publish\%%F" (
        echo Missing publish output: %%F 1>&2
        goto :failed
    )
)
if not exist "%stage%\publish\*.exe" (
    echo Missing launcher executable. 1>&2
    goto :failed
)
for %%E in ("%stage%\publish\*.exe") do (
    for %%S in (dll deps.json runtimeconfig.json) do (
        if not exist "%stage%\publish\Runtime\%%~nE.%%S" (
            echo Missing publish output: Runtime\%%~nE.%%S 1>&2
            goto :failed
        )
    )
)
for %%F in (coreclr.dll PresentationFramework.dll) do (
    if exist "%stage%\publish\Runtime\%%F" (
        echo Unexpected bundled framework: %%F 1>&2
        goto :failed
    )
)

powershell.exe -NoProfile -NonInteractive -Command ^
    "$ErrorActionPreference = 'Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
    "[AppContext]::SetSwitch('Switch.System.IO.Compression.ZipFile.UseBackslash', $false);" ^
    "[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $env:stage 'publish'), (Join-Path $env:stage $env:archive), [IO.Compression.CompressionLevel]::Optimal, $false, [Text.Encoding]::UTF8)" || goto :failed
move /y "%stage%\%archive%" "%out%\%archive%" >nul || goto :failed
echo Package: "%out%\%archive%"
set "result=0"
goto :cleanup

:failed
echo Packaging failed. See the error above. 1>&2

:cleanup
for %%D in ("%stage%\..") do if /i "%%~fD"=="%out%" rd /s /q "%stage%"
:done
popd
exit /b %result%
