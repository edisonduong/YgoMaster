@echo off

set "SOURCE=C:\Users\Edison\Repos\YgoMaster\YgoMaster"
set "DEST=C:\Program Files (x86)\Steam\steamapps\common\Yu-Gi-Oh!  Master Duel\YgoMaster"

echo Copying YgoMaster files...
echo.

robocopy "%SOURCE%" "%DEST%" /E /XD "%SOURCE%\Data" /R:2 /W:1

if %ERRORLEVEL% GEQ 8 (
    echo.
    echo ERROR: Copy failed.
    pause
    exit /b 1
)

echo.
echo Copy completed successfully.
echo Starting YgoMaster...

cd /d "%DEST%"

start "" "YgoMaster.exe"
start "" "YgoMasterClient.exe"

echo.
echo YgoMaster started successfully.
pause