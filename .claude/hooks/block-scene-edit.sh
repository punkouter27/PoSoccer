#!/usr/bin/env bash
# ============================================================================
# block-scene-edit.sh — BLOCKING HOOK
# Prevents Claude from directly editing .unity, .prefab, and .asset YAML files.
# These files contain serialized references that break when text-edited.
# Use an Editor_ method run headless (scripts/unity-exec.ps1 -Method ...) instead.
# ============================================================================
# Trigger: PreToolUse on Edit|Write
# Exit: 2 = block, 0 = allow
# ============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOOK_PROFILE_LEVEL="minimal"
source "${SCRIPT_DIR}/_lib.sh"

# Read the tool input from stdin (JSON with tool_name, file_path, etc.)
INPUT=$(cat)

# Extract the file path from the tool input
FILE_PATH=$(echo "$INPUT" | jq -r '.tool_input.file_path // empty')

if [ -z "$FILE_PATH" ]; then
    exit 0
fi

# Check if the file has a Unity binary/YAML extension
case "$FILE_PATH" in
    *.unity|*.prefab)
        MSG="Direct editing of scene/prefab files corrupts serialized references."
        echo "" >&2
        echo "  File: $FILE_PATH" >&2
        echo "" >&2
                echo "  Instead, write an Editor_ method and run it headless:" >&2
        echo "    scripts/unity-exec.ps1 -Method PoSoccer.EditorTools.Editor_X.Run" >&2
        unity_hook_block "$MSG"
        ;;
    *.asset)
        # Allow .asset files in Scripts/ or code-generated paths, block others
        case "$FILE_PATH" in
            */Scripts/*|*/Editor/*|*/Plugins/*)
                exit 0
                ;;
            *)
                MSG="Direct editing of .asset files can corrupt serialized data."
                echo "" >&2
                echo "  File: $FILE_PATH" >&2
                echo "" >&2
                                echo "  Instead, write an Editor_ method and run it headless:" >&2
                echo "    scripts/unity-exec.ps1 -Method PoSoccer.EditorTools.Editor_X.Run" >&2
                unity_hook_block "$MSG"
                ;;
        esac
        ;;
    *)
        exit 0
        ;;
esac
