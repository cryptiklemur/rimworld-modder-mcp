#!/usr/bin/env node

const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { execFileSync, spawnSync } = require("node:child_process");

const version = process.argv[2];

if (!version) {
  console.error("Expected release version argument.");
  process.exit(1);
}

const repoRoot = path.resolve(__dirname, "..");
const projectFile = path.join(repoRoot, "src", "RimWorldModderMcp", "RimWorldModderMcp.csproj");
const mcpbDir = path.join(repoRoot, "mcpb");
const tempDir = path.join(repoRoot, "temp");
const artifactsDir = path.join(repoRoot, "artifacts");
const nugetDir = path.join(artifactsDir, "nuget");
const packageId = "cryptiklemur.rimworld-modder-mcp";
const bundleName = `rimworld-modder-mcp-v${version}`;
const bundleDir = path.join(artifactsDir, bundleName);
const archiveName = `${bundleName}-dotnet.zip`;
const archivePath = path.join(artifactsDir, archiveName);
const checksumPath = `${archivePath}.sha256`;
const nupkgName = `${packageId}.${version}.nupkg`;
const nupkgPath = path.join(nugetDir, nupkgName);

function run(command, args, options = {}) {
  execFileSync(command, args, {
    cwd: repoRoot,
    stdio: "inherit",
    ...options
  });
}

function commandExists(command) {
  const checkCommand = process.platform === "win32" ? "where" : "which";
  const result = spawnSync(checkCommand, [command], { stdio: "ignore" });
  return result.status === 0;
}

function writeJson(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function createQuickstart(targetDir) {
  const lines = [
    `# RimWorld Modder MCP v${version}`,
    "",
    "1. Install the .NET 10 runtime: https://dotnet.microsoft.com/download/dotnet/10.0/runtime",
    "2. Extract this bundle somewhere permanent.",
    "3. Point your MCP client at `RimWorldModderMcp.dll` with the `dotnet` command.",
    "",
    "This generic stdio config works for Claude Desktop and most other agent CLIs that accept raw `mcpServers` JSON.",
    "",
    "Generic MCP config:",
    "",
    "```json",
    "{",
    '  "mcpServers": {',
    '    "rimworld-modder": {',
    '      "command": "dotnet",',
    '      "args": [',
    '        "/absolute/path/to/RimWorldModderMcp.dll",',
    '        "--rimworld-path=/absolute/path/to/RimWorld",',
    '        "--mod-dirs=/absolute/path/to/RimWorld/Mods"',
    "      ]",
    "    }",
    "  }",
    "}",
    "```",
    "",
    "Notes:",
    "- `--rimworld-path` is required.",
    "- `--mod-dirs` is optional; use a comma-separated list when you want workshop/custom mod directories too.",
    "- This bundle also includes `manifest.json` for MCP package consumers that understand package manifests."
  ];

  fs.writeFileSync(path.join(targetDir, "QUICKSTART.md"), `${lines.join("\n")}\n`, "utf8");
}

function createExampleConfigs(targetDir) {
  const examplesDir = path.join(targetDir, "examples");
  fs.mkdirSync(examplesDir, { recursive: true });

  writeJson(path.join(examplesDir, "generic-mcp-config.posix.json"), {
    mcpServers: {
      "rimworld-modder": {
        command: "dotnet",
        args: [
          "/absolute/path/to/rimworld-modder-mcp/RimWorldModderMcp.dll",
          "--rimworld-path=/absolute/path/to/RimWorld",
          "--mod-dirs=/absolute/path/to/RimWorld/Mods"
        ]
      }
    }
  });

  writeJson(path.join(examplesDir, "generic-mcp-config.windows.json"), {
    mcpServers: {
      "rimworld-modder": {
        command: "dotnet",
        args: [
          "C:\\path\\to\\rimworld-modder-mcp\\RimWorldModderMcp.dll",
          "--rimworld-path=C:\\path\\to\\RimWorld",
          "--mod-dirs=C:\\path\\to\\RimWorld\\Mods"
        ]
      }
    }
  });
}

function createArchive(sourceDir, destinationPath) {
  if (fs.existsSync(destinationPath)) {
    fs.rmSync(destinationPath, { force: true });
  }

  if (process.platform === "win32") {
    run(
      "powershell.exe",
      [
        "-NoProfile",
        "-Command",
        `Compress-Archive -Path '${path.basename(sourceDir)}' -DestinationPath '${path.basename(destinationPath)}' -Force`
      ],
      { cwd: path.dirname(sourceDir) }
    );
    return;
  }

  if (!commandExists("zip")) {
    console.error("zip is required to create the release archive.");
    process.exit(1);
  }

  run("zip", ["-rq", path.basename(destinationPath), path.basename(sourceDir)], {
    cwd: path.dirname(sourceDir)
  });
}

fs.rmSync(tempDir, { recursive: true, force: true });
fs.rmSync(artifactsDir, { recursive: true, force: true });
fs.mkdirSync(artifactsDir, { recursive: true });
fs.mkdirSync(nugetDir, { recursive: true });

run("dotnet", [
  "pack",
  projectFile,
  "-c",
  "Release",
  "-o",
  nugetDir,
  `-p:Version=${version}`
]);

run("dotnet", [
  "publish",
  projectFile,
  "-c",
  "Release",
  "-o",
  tempDir,
  `-p:Version=${version}`
]);

if (!fs.existsSync(nupkgPath)) {
  console.error(`Expected ${nupkgPath} to exist after pack.`);
  process.exit(1);
}

const requiredFiles = [
  "manifest.json",
  "RimWorldModderMcp.dll",
  "RimWorldModderMcp.deps.json",
  "RimWorldModderMcp.runtimeconfig.json"
];

for (const file of requiredFiles) {
  const filePath = path.join(mcpbDir, file);
  if (!fs.existsSync(filePath)) {
    console.error(`Expected ${filePath} to exist after publish.`);
    process.exit(1);
  }
}

fs.cpSync(mcpbDir, bundleDir, { recursive: true });

const manifestPath = path.join(bundleDir, "manifest.json");
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
manifest.version = version;
writeJson(manifestPath, manifest);

createQuickstart(bundleDir);
createExampleConfigs(bundleDir);
createArchive(bundleDir, archivePath);

const checksum = crypto
  .createHash("sha256")
  .update(fs.readFileSync(archivePath))
  .digest("hex");

fs.writeFileSync(checksumPath, `${checksum}  ${archiveName}\n`, "utf8");

console.log(`Created ${archivePath}`);
console.log(`Created ${checksumPath}`);
console.log(`Created ${nupkgPath}`);
