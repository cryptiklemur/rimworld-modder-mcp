#!/usr/bin/env node

const fs = require("node:fs");
const path = require("node:path");
const { execFileSync } = require("node:child_process");

const version = process.argv[2];
const apiKey = process.env.NUGET_TRUSTED_PUBLISHING_TOKEN;

if (!version) {
  console.error("Expected release version argument.");
  process.exit(1);
}

if (!apiKey) {
  console.error("NUGET_TRUSTED_PUBLISHING_TOKEN is required. In GitHub Actions, provide it from NuGet/login@v1 via Trusted Publishing.");
  process.exit(1);
}

const repoRoot = path.resolve(__dirname, "..");
const packageId = "cryptiklemur.rimworld-modder-mcp";
const packagePath = path.join(repoRoot, "artifacts", "nuget", `${packageId}.${version}.nupkg`);

if (!fs.existsSync(packagePath)) {
  console.error(`Expected ${packagePath} to exist before publishing.`);
  process.exit(1);
}

execFileSync("dotnet", [
  "nuget",
  "push",
  packagePath,
  "--api-key",
  apiKey,
  "--source",
  "https://api.nuget.org/v3/index.json",
  "--skip-duplicate"
], {
  cwd: repoRoot,
  stdio: "inherit"
});

console.log(`Published ${packagePath}`);
