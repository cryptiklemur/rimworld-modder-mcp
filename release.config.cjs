module.exports = {
  branches: ["main", "master"],
  tagFormat: "v${version}",
  plugins: [
    "@semantic-release/commit-analyzer",
    "@semantic-release/release-notes-generator",
    [
      "@semantic-release/exec",
      {
        prepareCmd: "node scripts/prepare-release-bundle.cjs ${nextRelease.version}",
        publishCmd: "node scripts/publish-nuget-tool.cjs ${nextRelease.version}"
      }
    ],
    [
      "@semantic-release/github",
      {
        successComment: false,
        failComment: false,
        assets: [
          {
            path: "artifacts/rimworld-modder-mcp-v${nextRelease.version}-dotnet.zip",
            label: "Portable .NET MCP bundle"
          },
          {
            path: "artifacts/rimworld-modder-mcp-v${nextRelease.version}-dotnet.zip.sha256",
            label: "SHA-256 checksum"
          },
          {
            path: "artifacts/nuget/cryptiklemur.rimworld-modder-mcp.${nextRelease.version}.nupkg",
            label: ".NET tool package"
          }
        ]
      }
    ]
  ]
};
