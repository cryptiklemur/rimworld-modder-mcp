#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd "$script_dir/.." && pwd)
project="$repo_root/src/RimWorldModderMcp/RimWorldModderMcp.csproj"
fixture_template="$repo_root/tests/fixtures/smoke-template"
build_output="$repo_root/src/RimWorldModderMcp/bin/Debug/net10.0/RimWorldModderMcp.dll"

workdir=""
pass_count=0
test_count=0

cleanup() {
  if [[ -n "${mcp_pid:-}" ]]; then
    kill "$mcp_pid" >/dev/null 2>&1 || true
    wait "$mcp_pid" >/dev/null 2>&1 || true
  fi

  if [[ -n "$workdir" && -d "$workdir" ]]; then
    rm -rf "$workdir"
  fi
}

trap cleanup EXIT

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

ensure_built() {
  if [[ "${SKIP_BUILD:-0}" == "1" ]]; then
    printf 'Skipping build because SKIP_BUILD=1.\n' >&2
    return
  fi

  printf 'Building project...\n' >&2
  dotnet build "$project" --no-restore >/dev/null
}

stage_fixture() {
  workdir=$(mktemp -d /tmp/rwmcp-smoke.XXXXXX)
  cp -R "$fixture_template/." "$workdir/"

  mkdir -p \
    "$workdir/Mods/Alpha/Textures/Weapons" \
    "$workdir/Mods/Alpha/Textures/Buildings" \
    "$workdir/Mods/Alpha/Textures/Widgets" \
    "$workdir/Mods/Alpha/Sounds" \
    "$workdir/Mods/Beta/Textures/Weapons" \
    "$workdir/notes"

  truncate -s 4096 "$workdir/Mods/Alpha/Textures/Weapons/AlphaBlade.png"
  truncate -s 4096 "$workdir/Mods/Alpha/Textures/Weapons/AlphaBladeTwin.png"
  truncate -s 4096 "$workdir/Mods/Alpha/Textures/Weapons/SharedSwordAlpha.png"
  truncate -s 4096 "$workdir/Mods/Alpha/Textures/Buildings/AlphaWorkbench.png"
  truncate -s 4096 "$workdir/Mods/Alpha/Textures/Widgets/AlphaBrokenWidget.png"
  truncate -s 4096 "$workdir/Mods/Beta/Textures/Weapons/BetaBlade.png"
  truncate -s 4096 "$workdir/Mods/Beta/Textures/Weapons/BetaIdolBlade.png"
  truncate -s 4096 "$workdir/Mods/Beta/Textures/Weapons/SharedSwordBeta.png"
  truncate -s 2048 "$workdir/Mods/Alpha/Sounds/UnusedHum.ogg"
  truncate -s 2097152 "$workdir/Mods/Alpha/Textures/Weapons/HugeUnusedBlade.png"

  git -C "$workdir" init -q
  git -C "$workdir" config core.autocrlf false
  git -C "$workdir" config user.name "Smoke Test"
  git -C "$workdir" config user.email "smoke@example.com"
  git -C "$workdir" add .
  git -C "$workdir" commit -q -m "baseline"

  printf '\n<!-- changed for smoke test -->\n' >> "$workdir/Mods/Alpha/Defs/AlphaDefs.xml"
  printf '\n<!-- changed for smoke test -->\n' >> "$workdir/Mods/Beta/Patches/BetaPatches.xml"
  printf 'remember to verify release bundle\n' > "$workdir/notes/todo.txt"

  cat > "$workdir/.rimworld-modder-mcp.json" <<EOF
{
  "projectRoot": "$workdir",
  "rimworldPath": "$workdir/RimWorld",
  "modDirs": ["$workdir/Mods"],
  "logPath": "$workdir/logs/Player.log",
  "allowedDlcs": "Core,Biotech",
  "outputMode": "normal",
  "pageSize": 25,
  "pageOffset": 0,
  "handleResults": false,
  "logLevel": "Warning"
}
EOF
}

base_cmd=()

setup_base_cmd() {
  base_cmd=(
    dotnet
    "$build_output"
    --config "$workdir/.rimworld-modder-mcp.json"
    --project-root "$workdir"
    --log-level Warning
  )
}

run_json_command() {
  local label="$1"
  local jq_expr="$2"
  shift 2

  test_count=$((test_count + 1))
  printf '[%02d] %s\n' "$test_count" "$label" >&2

  local output
  if ! output=$("$@" 2>/dev/null); then
    fail "$label command failed"
  fi

  if ! printf '%s' "$output" | jq -e . >/dev/null 2>&1; then
    printf '%s\n' "$output" >&2
    fail "$label did not return valid JSON"
  fi

  if printf '%s' "$output" | jq -e 'has("error")' >/dev/null 2>&1; then
    printf '%s\n' "$output" >&2
    fail "$label returned an error payload"
  fi

  if ! printf '%s' "$output" | jq -e "$jq_expr" >/dev/null 2>&1; then
    printf '%s\n' "$output" >&2
    fail "$label did not satisfy jq expression: $jq_expr"
  fi

  pass_count=$((pass_count + 1))
}

run_tool() {
  local tool="$1"
  local jq_expr="$2"
  shift 2

  local -a cmd=("${base_cmd[@]}" --tool "$tool")
  local param
  for param in "$@"; do
    cmd+=(--param "$param")
  done

  run_json_command "$tool" "$jq_expr" "${cmd[@]}"
}

run_tool_cli() {
  local tool="$1"
  local jq_expr="$2"
  shift 2

  local -a cmd=("${base_cmd[@]}" --tool "$tool" "$@")
  run_json_command "$tool" "$jq_expr" "${cmd[@]}"
}

read_mcp_json() {
  local line
  while IFS= read -r line <&4; do
    if [[ -n "$line" ]]; then
      printf '%s' "$line"
      return 0
    fi
  done

  return 1
}

run_mcp_smoke() {
  printf '[%02d] mcp_protocol\n' $((test_count + 1)) >&2
  test_count=$((test_count + 1))

  coproc MCPPROC { "${base_cmd[@]}" 2>/dev/null; }
  mcp_pid=$MCPPROC_PID
  exec 3>&${MCPPROC[1]} 4<&${MCPPROC[0]}

  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"smoke-test","version":"1.0.0"}}}' >&3
  local initialize_response
  initialize_response=$(read_mcp_json) || fail "mcp initialize response missing"
  printf '%s' "$initialize_response" | jq -e '.result.serverInfo.name == "rimworld-modder-mcp"' >/dev/null 2>&1 || fail "mcp initialize invalid"

  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}' >&3

  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' >&3
  local tools_list
  tools_list=$(read_mcp_json) || fail "tools/list response missing"
  printf '%s' "$tools_list" | jq -e '
    (.result.tools | length) >= 61 and
    any(.result.tools[]; .name == "get_statistics") and
    any(.result.tools[]; .name == "get_result_by_handle")
  ' >/dev/null 2>&1 || fail "unexpected MCP tool list"

  printf '%s\n' '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_statistics","arguments":{"outputMode":"compact","handleResults":true}}}' >&3
  local handled_response
  handled_response=$(read_mcp_json) || fail "get_statistics MCP response missing"

  local handle
  handle=$(printf '%s' "$handled_response" | jq -r '.result.content[0].text | fromjson | ._meta.resultHandle')
  [[ -n "$handle" && "$handle" != "null" ]] || fail "MCP get_statistics did not return a result handle"

  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"get_result_by_handle\",\"arguments\":{\"handle\":\"$handle\"}}}" >&3
  local handle_response
  handle_response=$(read_mcp_json) || fail "get_result_by_handle MCP response missing"
  printf '%s' "$handle_response" | jq -e --arg handle "$handle" '.result.content[0].text | fromjson | .handle == $handle and (.result.totalMods >= 5)' >/dev/null 2>&1 || fail "get_result_by_handle MCP response invalid"

  exec 3>&- 4<&-
  kill "$mcp_pid" >/dev/null 2>&1 || true
  wait "$mcp_pid" >/dev/null 2>&1 || true
  unset mcp_pid

  pass_count=$((pass_count + 1))
}

ensure_built
stage_fixture
setup_base_cmd

run_tool_cli get_statistics '._meta.tool == "get_statistics" and .loadingStatus.isFullyLoaded == true and .totalMods >= 5 and .totalDefs >= 10' --output-mode compact --page-size 5
run_tool get_conflicts '._meta.tool == "get_conflicts" and .totalConflicts >= 1'
run_tool get_patch_conflicts '._meta.tool == "get_patch_conflicts" and .totalConflicts >= 1'
run_tool trace_patch_application '._meta.tool == "trace_patch_application" and .defName == "SharedSword" and .totalPatches >= 2' 'defName=SharedSword'
run_tool get_patch_performance '._meta.tool == "get_patch_performance" and .totalPatches >= 1 and (.analysis | length) >= 1' 'modPackageId=example.beta'
run_tool write_xpath '._meta.tool == "write_xpath" and .defName == "SharedSword" and (.suggestions | length) >= 1' 'defName=SharedSword' 'targetElement=label'
run_tool preview_patch '._meta.tool == "preview_patch" and (.patchXml | length) > 0 and (.affectedDefinitions | length) >= 1' 'xpath=//Defs/ThingDef[defName="SharedSword"]/label' 'operation=Replace' 'value=<label>smoke patched sword</label>'
run_tool preview_patch_result '._meta.tool == "preview_patch_result" and .defName == "SharedSword" and .appliedPatches >= 1' 'defName=SharedSword'
run_tool validate_localization '._meta.tool == "validate_localization" and .totalModsWithTranslations >= 1' 'modPackageId=example.alpha'
run_tool find_unused_assets '._meta.tool == "find_unused_assets" and .totalUnusedAssets >= 1' 'modPackageId=example.alpha'
run_tool lint_xml '._meta.tool == "lint_xml" and .definitionsWithIssues >= 1' 'modPackageId=example.alpha' 'severityLevel=info'
run_tool generate_documentation '._meta.tool == "generate_documentation" and .modPackageId == "example.alpha" and .documentationSize > 0' 'modPackageId=example.alpha'
run_tool create_compatibility_report '._meta.tool == "create_compatibility_report" and .summary.totalModsAnalyzed >= 1 and (.fullReport | length) > 0' 'modPackageId=example.beta'
run_tool export_definitions '._meta.tool == "export_definitions" and .exportedCount >= 1 and (.exportedData | length) > 0' 'format=json' 'defType=ThingDef' 'modPackageId=example.alpha' 'maxDefinitions=10'
run_tool analyze_balance '._meta.tool == "analyze_balance" and .totalAnalyzed >= 1 and (.balanceAnalysis | length) >= 1' 'defType=ThingDef'
run_tool get_recipe_chains '._meta.tool == "get_recipe_chains" and .targetItem == "AlphaBlade" and .totalChains >= 1' 'targetDefName=AlphaBlade'
run_tool find_research_paths '._meta.tool == "find_research_paths" and .targetResearch == "AlphaSmithing" and .totalPaths >= 1' 'targetResearch=AlphaSmithing'
run_tool get_biome_compatibility '._meta.tool == "get_biome_compatibility" and .totalBiomes >= 1 and (.biomeAnalysis | length) >= 1' 'biomeDefName=TemperateForest'
run_tool calculate_room_requirements '._meta.tool == "calculate_room_requirements" and .targetBuilding == "AlphaWorkbench" and .buildingStats.MarketValue >= 1' 'targetDefName=AlphaWorkbench'
run_tool suggest_def_name '._meta.tool == "suggest_def_name" and (.suggestions | length) >= 3' 'defType=ThingDef' 'baseName=alpha blade mk ii' 'modPackageId=example.alpha'
run_tool check_naming_conventions '._meta.tool == "check_naming_conventions" and .isValid == false and (.violations | length) >= 1' 'defName=bad name!' 'defType=ThingDef'
run_tool find_translation_keys '._meta.tool == "find_translation_keys" and .statistics.totalKeys >= 1' 'modPackageId=example.alpha'
run_tool generate_mod_metadata '._meta.tool == "generate_mod_metadata" and .packageId == "smoke.author.smoke.blade.pack" and (.files | length) >= 3' 'modName=Smoke Blade Pack' 'author=Smoke Author' 'description=Generated metadata for smoke testing' 'rimworldVersion=1.6' 'packageId=smoke.author.smoke.blade.pack' 'dependencies=example.alpha,example.beta'
run_tool check_version_compatibility '._meta.tool == "check_version_compatibility" and .summary.totalModsChecked >= 1' 'modPackageId=example.alpha' 'targetVersion=1.6'
run_tool suggest_load_order '._meta.tool == "suggest_load_order" and .totalMods >= 2 and (.suggestedOrder | length) >= 2' 'modPackageIds=example.alpha,example.beta'
run_tool calculate_market_value '._meta.tool == "calculate_market_value" and .defName == "AlphaBlade" and .calculation.finalValue > 0' 'defName=AlphaBlade'
run_tool analyze_mod_compatibility '._meta.tool == "analyze_mod_compatibility" and .mod1.packageId == "example.alpha" and .mod2.packageId == "example.beta"' 'modPackageId1=example.alpha' 'modPackageId2=example.beta'
run_tool get_mod_dependencies '._meta.tool == "get_mod_dependencies" and .mod.packageId == "example.beta"' 'modPackageId=example.beta'
run_tool find_broken_references '._meta.tool == "find_broken_references" and .totalBrokenReferences >= 1' 'modPackageId=example.alpha'
run_tool validate_mod_structure '._meta.tool == "validate_mod_structure" and .mod.packageId == "example.alpha"' 'modPackageId=example.alpha'
run_tool validate_def '._meta.tool == "validate_def" and .defName == "AlphaBrokenWidget"' 'defName=AlphaBrokenWidget'
run_tool get_def_dependencies '._meta.tool == "get_def_dependencies" and .defName == "AlphaBlade" and .totalDependencies >= 1' 'defName=AlphaBlade'
run_tool validate_xpath '._meta.tool == "validate_xpath" and .isValid == true and .summary.syntaxValid == true' 'xpath=//label' 'defName=SharedSword'
run_tool get_def '._meta.tool == "get_def" and .defName == "AlphaBlade" and .mod.packageId == "example.alpha"' 'defName=AlphaBlade'
run_tool get_defs_by_type '._meta.tool == "get_defs_by_type" and .type == "ThingDef" and .count >= 1' 'type=ThingDef'
run_tool_cli search_defs '._meta.tool == "search_defs" and .count >= 1 and (.results | length) >= 1' --param 'searchTerm=blade' --param 'inType=ThingDef' --output-mode compact --page-size 2 --page-offset 0
run_tool get_def_inheritance_tree '._meta.tool == "get_def_inheritance_tree" and .defName == "AlphaBlade" and .totalLevels >= 2' 'defName=AlphaBlade'
run_tool get_patches_for_def '._meta.tool == "get_patches_for_def" and .defName == "SharedSword" and .totalPatches >= 1' 'defName=SharedSword'
run_tool compare_defs '._meta.tool == "compare_defs" and .totalDifferences >= 1' 'defName1=AlphaBlade' 'defName2=BetaBlade'
run_tool get_abstract_defs '._meta.tool == "get_abstract_defs" and .totalAbstractDefs >= 1' 'type=ThingDef'
run_tool get_mod_list '._meta.tool == "get_mod_list" and .totalMods >= 5 and (.mods | length) >= 5'
run_tool get_references '._meta.tool == "get_references" and .searchedFor == "SteelIngot" and .totalReferences >= 1' 'defName=SteelIngot'
run_tool find_duplicate_content '._meta.tool == "find_duplicate_content" and .totalDuplicates >= 1' 'defType=ThingDef' 'similarityThreshold=0.75'
run_tool suggest_optimizations '._meta.tool == "suggest_optimizations" and .modsWithOptimizations >= 1' 'modPackageId=example.alpha'
run_tool analyze_texture_usage '._meta.tool == "analyze_texture_usage" and .totalTextures >= 1 and .totalSizeMB > 1' 'modPackageId=example.alpha'
run_tool triage_player_log '._meta.tool == "triage_player_log" and .summary.totalIncidents >= 1 and (.groups | length) >= 1' "logPath=$workdir/logs/Player.log"
run_tool validate_def_against_runtime '._meta.tool == "validate_def_against_runtime" and .status == "warn" and .summary.warnings >= 1' 'defName=AlphaBrokenWidget'
run_tool scan_dlc_dependencies '._meta.tool == "scan_dlc_dependencies" and .checkedMods >= 2 and .totalFindings >= 1' 'allowedDlcs=Core,Biotech'
run_tool audit_scope '._meta.tool == "audit_scope" and .scope.type == "mod" and (.findings | length) >= 1' 'scopeType=mod' 'scopeValue=example.beta' 'severity=info'
run_tool triage_patch_conflicts '._meta.tool == "triage_patch_conflicts" and .summary.hotspots >= 1' 'modPackageId=example.beta' 'severity=info'
run_tool content_coverage_report '._meta.tool == "content_coverage_report" and .scope.modPackageId == "example.alpha" and .summary.totalDefs >= 1' 'modPackageId=example.alpha'
run_tool mod_ready_check '._meta.tool == "mod_ready_check" and .summary.checkedMods >= 1 and (.modStatuses | length) >= 1' 'modPackageId=example.beta' "logPath=$workdir/logs/Player.log"
run_tool doctor '._meta.tool == "doctor" and (.checks | length) >= 5 and .summary.projectRoot == "'"$workdir"'"'
run_tool audit_changed_files '._meta.tool == "audit_changed_files" and .summary.changedFiles >= 2 and .summary.matchedDefs >= 1 and .summary.matchedPatches >= 1' 'severity=info'
run_tool validate_changed_content '._meta.tool == "validate_changed_content" and .summary.changedDefs >= 1 and .summary.changedPatches >= 1' 
run_tool compare_player_logs '._meta.tool == "compare_player_logs" and .summary.newGroups >= 1 and .summary.resolvedGroups >= 1' "logPath=$workdir/logs/Player.log" "otherLogPath=$workdir/logs/Player-prev.log"
run_tool find_patch_hotspots '._meta.tool == "find_patch_hotspots" and .summary.hotspots >= 1 and (.hotspots | length) >= 1' 'severity=info'
run_tool broken_reference_explainer '._meta.tool == "broken_reference_explainer" and .summary.sourceDefs >= 1 and (.likelyCauses | length) >= 1' 'reference=MissingRelic'
run_tool scope_search '._meta.tool == "scope_search" and .summary.matchedDefs >= 1' 'scopeType=mod' 'scopeValue=example.alpha' 'searchTerm=blade'
run_tool load_order_impact_report '._meta.tool == "load_order_impact_report" and .summary.impactedMods >= 1' 'modPackageId=example.beta' 'moveBeforeModPackageId=example.alpha'

run_mcp_smoke

printf 'PASS: %d/%d checks succeeded\n' "$pass_count" "$test_count"
