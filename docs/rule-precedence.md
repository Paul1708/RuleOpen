# RuleOpen Rule Precedence & Evaluation Algorithm

RuleOpen uses a single, deterministic, platform-independent evaluation engine (`RuleOpen.Core.Rules.RuleEngine`) shared identically by:

- **`RuleOpen.Dispatcher.exe`** (live file opening)
- **`RuleOpen.Desktop.exe` — Test a file** (diagnostics and simulation)

## 1. Input & Path Normalization

1. **Path sanitization (`PathNormalizer.TryNormalizeFilePath`)**:
   - Trims surrounding whitespace and quotation marks.
   - Replaces `/` with `\`.
   - Rejects empty strings, relative paths (`!Path.IsPathFullyQualified`), wildcards (`*`, `?`), device paths (`\\.\`, `\\?\`), trailing directory separators, and directory-only inputs.
   - Resolves `.` and `..` segments cleanly via `Path.GetFullPath`.
   - Extracts:
     - `NormalizedFilePath`
     - `NormalizedParentFolder` (trailing backslash stripped except for drive roots `D:\` and UNC share roots `\\server\share`)
     - `NormalizedExtension` (lowercased via `ExtensionNormalizer`, e.g., `.PDF` → `.pdf`)

2. **Directory boundary protection**:
   - Folder matching compares `EnsureTrailingSeparator(parentFolder)` against `EnsureTrailingSeparator(ruleFolder)` using `StringComparison.OrdinalIgnoreCase`.
   - Therefore, a rule scoped to `C:\Test\Work` normalizes its boundary to `C:\Test\Work\`, guaranteeing that `C:\Test\Workspace\file.pdf` (`C:\Test\Workspace\`) **never** matches merely because of a shared string prefix.

3. **Folder depth calculation (`PathNormalizer.GetFolderDepth`)**:
   - Drive root `D:\` → depth `1`
   - `D:\Projects` → depth `2`
   - `D:\Projects\ClientA` → depth `3`
   - UNC root `\\server\share` → depth `1`
   - `\\server\share\Docs` → depth `2`

## 2. Candidate Filtering

Every configured rule is evaluated independently:

- Disabled rules (`Enabled == false`) are excluded.
- Rules whose normalized `Extensions` do not contain `NormalizedExtension` are excluded.
- Exact folder match (`NormalizedRuleFolder == NormalizedParentFolder`) always matches.
- Subfolder match matches only if `IncludeSubfolders == true` and `NormalizedParentFolder` is strictly inside `NormalizedRuleFolder`.

## 3. Deterministic Precedence Order (`RulePrecedenceComparer`)

Matching candidates are sorted by a strict total order that **never depends on JSON array order or rule insertion order**:

### Mode A: `SpecificityFirst` (Default)

1. **Folder depth descending**: deeper folders beat shallower ancestors (`D:\Projects\ClientA` beats `D:\Projects`).
2. **Exact folder match first**: exact-folder matches beat recursive matches at equal depth.
3. **Explicit user `Priority` descending**: higher numeric priority wins (default `100`, range `0–10,000`).
4. **Extension specificity ascending**: narrower extension lists beat broader ones (`[.pdf]` beats `[.pdf, .txt, .md]`).
5. **Rule ID ascending (`StringComparison.Ordinal`)**: guarantees a total, deterministic tie-breaker.

### Mode B: `PriorityFirst` (Optional Setting)

Swaps step 3 (`Priority` descending) ahead of steps 1–2 (`FolderDepth` and exactness), keeping steps 4–5 identical.

## 4. Fallback & Failure Handling

When **no rule matches**, `DispatchCoordinator` checks:

1. **Per-extension fallback** (`ExtensionFallbacks[ext]`), captured automatically when RuleOpen registers the extension and customizable at any time in **Settings → Fallback applications**.
2. **Configured global fallback** (`GlobalFallback`).
3. If neither exists, `RuleOpen.Dispatcher` launches the recovery picker rather than guessing an unrelated app.

If a matched rule's **target application is missing**, RuleOpen **never** silently opens the file in a different app; it surfaces a clear recovery dialog so the user can fix or re-pick the application.

> **Explorer Icon Routing Consistency:** When **File icons in Explorer** is set to **Use matched routed app icon (Recommended)**, `PerUserFileAssociationService` sorts enabled rules with the exact same `RulePrecedenceComparer` into `%LocalAppData%\RuleOpen\icon-routes.tsv`, ensuring Windows File Explorer's `IExtractIconW` shell handler displays the same target app icon that double-clicking will launch.
