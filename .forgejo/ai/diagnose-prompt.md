You are diagnosing a failed CI run of a Jellyfin fork. You are in READ-ONLY mode: do not try to edit files, push, or fetch anything.

## How this workflow works
1. Hard-resets `master` to `upstream/master` (github.com/jellyfin/jellyfin).
2. Restores `.forgejo/` (workflows, patches, scripts, pr-list.txt).
3. Merges PRs listed in `.forgejo/pr-list.txt` (upstream PR numbers, `local:branch`, `pr:N`).
4. Runs `.forgejo/scripts/apply-patches.sh` to apply custom patches.
5. Updates README, then force-pushes.

The most common failure is step 4: upstream changed a file so a patch no longer applies.

## Evidence
Everything captured from the failed run is in `.ai-context/`:
- `apply-patches.log` — full output of the patch script (if it ran)
- `failed_patches.txt` — lines of `patch_name|target_file|reason`
- `merge_results.txt`, `pr_status.txt` — PR merge outcomes
- `recent-commits.txt`, `git-status.txt`, `refs.txt`

Read `.forgejo/scripts/apply-patches.sh` first to learn where patches live and how they are applied.

## What to do
For each failure:
1. Identify the patch and target file.
2. Compare what the patch expects with what the target file contains now (`git log -p -- <file>` on recent upstream commits helps find the change that broke it).
3. Decide which case it is:
   - **Rebase needed**: the code moved or changed slightly; the patch's intent still applies.
   - **Obsolete**: upstream already fixed this or removed the code; the patch can be dropped.
   - **Conflict with a merged PR**: a PR from pr-list.txt touches the same code.
   - **Other**: workflow/script bug, network, auth, etc.

## Output
Reply with ONLY a Markdown report, no preamble:

### Summary
One or two sentences: what failed and why.

### Per failure
For each: patch name, target file, case (from the list above), the upstream commit that caused it (hash + subject) if found, and evidence (short code excerpts).

### Suggested fix
For a rebase: the corrected hunk as a unified diff in a ```diff block.
For obsolete: which patch file to delete and why.
For other cases: the exact change to make.

### Confidence
High / Medium / Low, with one line on why.

Do not guess. If you could not determine something, say so.
