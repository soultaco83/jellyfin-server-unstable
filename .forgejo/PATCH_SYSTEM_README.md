# Custom Patch System for jellyfin-server-unstable

## Overview

This repository uses an automated patch system to preserve and document custom code modifications. The system automatically generates patches from committed changes and maintains them through upstream merges.

## How It Works

### 1. Patch Definitions (`patch-definitions.txt`)

The [patch-definitions.txt](patch-definitions.txt) file defines which custom modifications are tracked as patches.

Format: `patch-name.patch|file/path/to/modify.cs|Brief description`

Example:
```
syncplay-optimizations.patch|Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs|AI optimizations for SyncPlay session management
trickplay-optimizations.patch|Jellyfin.Server.Implementations/Trickplay/TrickplayManager.cs|Optimized trickplay generation
```

### 2. Patch Generation Script (`scripts/generate-patches.sh`)

The [generate-patches.sh](scripts/generate-patches.sh) script automatically creates patch files from your committed changes before upstream merges occur.

During the workflow run:
- Reads patch definitions from `patch-definitions.txt`
- Generates patches for any modified files
- Stores patches in `.github/patches/` directory

### 3. Workflow Integration

The [Reset to Upstream and Merge PRs.yml](workflows/Reset%20to%20Upstream%20and%20Merge%20PRs.yml) workflow has been enhanced with:

1. **Generate patches from custom modifications** (NEW)
   - Runs the patch generation script before upstream merge
   - Creates up-to-date patches from current modifications

2. **Backup custom files** (NEW)
   - Backs up patch definitions, scripts, and patch files

3. **Restore and commit patch files** (NEW)
   - Restores patch-related files after successful merge
   - Commits them to preserve them in the repository

## Current Custom Patches

### 1. SyncPlay Optimizations Patch
**File:** [patches/syncplay-optimizations.patch](patches/syncplay-optimizations.patch)
**Target:** `Emby.Server.Implementations/SyncPlay/SyncPlayManager.cs`

**Changes:**
- AI-driven optimizations for SyncPlay session management
- Improved async/await handling for session retrieval
- Enhanced session state management with stale session detection
- Better error handling and logging

### 2. SyncPlay Group Patch
**File:** [patches/syncplay-group.patch](patches/syncplay-group.patch)
**Target:** `Emby.Server.Implementations/SyncPlay/Group.cs`

**Changes:**
- Enhanced group management with better session handling
- Improved state synchronization between group members
- Optimized group update mechanisms

### 3. Trickplay Optimizations Patch
**File:** [patches/trickplay-optimizations.patch](patches/trickplay-optimizations.patch)
**Target:** `Jellyfin.Server.Implementations/Trickplay/TrickplayManager.cs`

**Changes:**
- Performance improvements for trickplay generation
- Resource usage optimizations
- Better handling of concurrent requests

### 4. Subtitle Encoder Optimizations Patch
**File:** [patches/subtitle-encoder-optimizations.patch](patches/subtitle-encoder-optimizations.patch)
**Target:** `MediaBrowser.MediaEncoding/Subtitles/SubtitleEncoder.cs`

**Changes:**
- Improved subtitle extraction performance
- Optimized encoding processes
- Reduced resource consumption during subtitle operations

### 5. Group Member Enhancements Patch
**File:** [patches/group-member-enhancements.patch](patches/group-member-enhancements.patch)
**Target:** `MediaBrowser.Controller/SyncPlay/GroupMember.cs`

**Changes:**
- Additional properties for enhanced group member tracking
- Improved session state management

### 6. PlayQueue Fix Patch
**File:** [patches/playqueue-fix.patch](patches/playqueue-fix.patch)
**Target:** `MediaBrowser.Controller/SyncPlay/Queue/PlayQueueManager.cs`

**Changes:**
- Bug fixes in play queue management
- Improved queue synchronization

### 7. Trickplay Options Patch
**File:** [patches/trickplay-options.patch](patches/trickplay-options.patch)
**Target:** `MediaBrowser.Model/Configuration/TrickplayOptions.cs`

**Changes:**
- Updated configuration options for trickplay
- New settings for optimization control

## Adding New Custom Patches

### Method 1: Automatic (Recommended)

1. Make your code changes and commit them to the repository
2. Add an entry to [patch-definitions.txt](patch-definitions.txt):
   ```
   my-new-feature.patch|path/to/modified/file.cs|Description of changes
   ```
3. The next workflow run will automatically generate the patch

### Method 2: Manual

1. Make your code changes
2. Generate the patch manually:
   ```bash
   git diff path/to/file.cs > .github/patches/my-feature.patch
   ```
3. Add entry to `patch-definitions.txt`
4. Commit both the patch file and the updated definitions file

## Testing Patches Locally

Test if a patch applies cleanly:

```bash
# Test without applying
git apply --check .github/patches/my-patch.patch

# Apply the patch
git apply .github/patches/my-patch.patch

# Or apply with git am (includes commit)
git am < .github/patches/my-patch.patch
```

## Workflow Behavior

Unlike the jellyfin-web repository which uses `git reset --hard`, this repository uses `git merge` for upstream updates. This means:

1. **Patches are documentation**: They document what custom changes exist
2. **Merge conflicts**: If upstream changes conflict with your modifications, the merge will fail with clear conflict markers
3. **Easy reapplication**: If you need to revert and reapply, patches make it simple
4. **Historical record**: Patches provide a clear history of customizations

## Benefits of This System

1. **Documentation**: Clear record of all custom modifications
2. **Version Control**: Patches are tracked in git, showing history of customizations
3. **Easy Review**: Anyone can see what customizations are applied by reading patch files
4. **Reapplication**: Simple to reapply changes if needed
5. **No Manual Intervention**: Workflow handles patch generation automatically
6. **Future-Proof**: Ready if you switch to reset-based workflow

## Troubleshooting

### Patch Generation Failed

If patch generation fails:
1. Check that the file path in `patch-definitions.txt` is correct
2. Verify the file has been modified and committed
3. Check the workflow logs for specific errors

### Merge Conflicts

If you encounter merge conflicts during upstream merge:
1. The workflow will stop and report conflicted files
2. Manually resolve conflicts locally
3. Your patches document what changes need to be preserved
4. Rerun the workflow after resolving conflicts

## Files Structure

```
.github/
├── patch-definitions.txt          # Defines which patches to manage
├── scripts/
│   └── generate-patches.sh        # Patch generation script
├── patches/                        # Generated patch files
│   ├── syncplay-optimizations.patch
│   ├── syncplay-group.patch
│   ├── trickplay-optimizations.patch
│   ├── subtitle-encoder-optimizations.patch
│   ├── group-member-enhancements.patch
│   ├── playqueue-fix.patch
│   └── trickplay-options.patch
└── workflows/
    └── Reset to Upstream and Merge PRs.yml  # Enhanced workflow
```

## Notes

- Patch files are regenerated on each workflow run if the source files have changes
- Existing patches are kept if no changes are detected
- The system complements the merge-based workflow
- Patches serve as documentation and backup of custom modifications
- All custom changes should be committed before workflow runs for proper patch generation
