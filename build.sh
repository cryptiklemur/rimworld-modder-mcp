#!/bin/bash
echo "Building RimWorld Modder MCP..."

echo ""
echo "Publishing framework-dependent package..."
dotnet publish src/RimWorldModderMcp/RimWorldModderMcp.csproj -c Release -o ./temp

echo ""
echo "Cleaning up temporary files..."
if [ -d "./temp" ]; then
    rm -rf ./temp
fi

echo ""
if [ -f "./mcpb/RimWorldModderMcp.dll" ] && [ -f "./mcpb/RimWorldModderMcp.runtimeconfig.json" ] && [ -f "./mcpb/RimWorldModderMcp.deps.json" ]; then
    echo "Successfully created framework-dependent package in mcpb directory"
else
    echo "Failed to create framework-dependent package in mcpb directory"
    exit 1
fi

echo ""
echo "Build complete! dotnet-launchable package is ready next to manifest.json in mcpb/"
