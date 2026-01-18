#!/bin/bash
# Script to apply patches after merging upstream changes
# This restores your custom modifications from patch files

set -e

PATCHES_DIR=".github/patches"
PATCH_DEFINITIONS_FILE=".github/patch-definitions.txt"

echo "=== Applying Custom Patches After Upstream Merge ==="

# Check if patches directory exists
if [ ! -d "$PATCHES_DIR" ]; then
    echo "⚠️  No patches directory found at $PATCHES_DIR"
    echo "No patches to apply."
    exit 0
fi

# Check if patch definitions file exists
if [ ! -f "$PATCH_DEFINITIONS_FILE" ]; then
    echo "⚠️  No patch definitions file found at $PATCH_DEFINITIONS_FILE"
    echo "No patches to apply."
    exit 0
fi

# Count patches
PATCH_COUNT=0
APPLIED_COUNT=0
FAILED_COUNT=0

# Read patch definitions and apply patches
while IFS='|' read -r patch_name file_path description; do
    # Skip empty lines and comments
    [[ -z "$patch_name" || "$patch_name" == \#* ]] && continue

    ((PATCH_COUNT++))

    echo ""
    echo "[$PATCH_COUNT] Applying: $patch_name"
    echo "  File: $file_path"
    echo "  Description: $description"

    # Check if patch file exists
    if [ ! -f "$PATCHES_DIR/$patch_name" ]; then
        echo "  ⚠️  Patch file not found: $PATCHES_DIR/$patch_name"
        ((FAILED_COUNT++))
        continue
    fi

    # Check if target file exists
    if [ ! -f "$file_path" ]; then
        echo "  ⚠️  Target file not found: $file_path"
        echo "  This might be normal if the file was removed or renamed upstream."
        ((FAILED_COUNT++))
        continue
    fi

    # Try to apply the patch
    if git apply --check "$PATCHES_DIR/$patch_name" 2>/dev/null; then
        # Patch applies cleanly
        git apply "$PATCHES_DIR/$patch_name"
        echo "  ✅ Applied successfully"
        ((APPLIED_COUNT++))
    else
        # Patch has conflicts, try with 3-way merge
        echo "  ⚠️  Patch has conflicts, attempting 3-way merge..."

        if git apply --3way "$PATCHES_DIR/$patch_name" 2>/dev/null; then
            echo "  ✅ Applied with 3-way merge"
            ((APPLIED_COUNT++))
        else
            echo "  ❌ Failed to apply patch (conflicts detected)"
            echo "  You may need to manually review this file: $file_path"
            ((FAILED_COUNT++))

            # Save failed patch info for summary
            echo "$patch_name|$file_path" >> /tmp/failed_patches.txt
        fi
    fi

done < "$PATCH_DEFINITIONS_FILE"

echo ""
echo "=== Patch Application Summary ==="
echo "Total patches: $PATCH_COUNT"
echo "Successfully applied: $APPLIED_COUNT"
echo "Failed: $FAILED_COUNT"

if [ $FAILED_COUNT -gt 0 ]; then
    echo ""
    echo "⚠️  Some patches failed to apply. Manual review may be required."
    if [ -f "/tmp/failed_patches.txt" ]; then
        echo "Failed patches:"
        while IFS='|' read -r patch_name file_path; do
            echo "  - $patch_name ($file_path)"
        done < /tmp/failed_patches.txt
    fi
    echo ""
    echo "Check git status to see conflicted files."
fi

# Exit with error if any patches failed (so workflow can handle it)
if [ $FAILED_COUNT -gt 0 ]; then
    exit 1
fi

echo ""
echo "✅ All patches applied successfully!"
