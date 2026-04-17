@echo off
echo Building RimWorld Modder MCP...

echo.
echo Publishing framework-dependent package...
dotnet publish src\RimWorldModderMcp\RimWorldModderMcp.csproj -c Release -o .\temp

echo.
echo Cleaning up temporary files...
if exist ".\temp" rmdir /s /q ".\temp"

echo.
if exist ".\mcpb\RimWorldModderMcp.dll" if exist ".\mcpb\RimWorldModderMcp.runtimeconfig.json" if exist ".\mcpb\RimWorldModderMcp.deps.json" (
    echo Successfully created framework-dependent package in mcpb
) else (
    echo Failed to create framework-dependent package in mcpb
    exit /b 1
)

echo.
echo Build complete! dotnet-launchable package is ready next to manifest.json
