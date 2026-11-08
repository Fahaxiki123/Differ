# Design QA

> 2026-10-24 目录整理：历史验证材料已迁到项目外，旧路径映射见 [归档索引](docs/ARCHIVE_INDEX.md)；正式程序与安装包仍保留原路径。

## 0.15.0 — 2026-09-25: application icon and offline installer

Released `artifacts/installers/Differ-Setup.exe` (52,942,228 bytes) and fixed self-contained application **0.15.0.0**. Specification **3.13.0** covers explicit optional SVN integration, current-user installation, safe uninstall ownership and Win10/11 x64 compatibility. The original dual-pane comparison UI/workflow is unchanged.

- Original graphite/blue paired-document icon generated with the built-in image tool, retained as PNG plus a nine-frame ICO (16 through 256 px). WPF decoder and small-size light/dark previews checked; EXE, both windows, Setup/Uninstall and installed shortcuts use the icon. Win10 toolbar glyph fallback added.
- Build: zero warnings/errors. **419 tests passed**, including installer maintenance and non-default TortoiseSVN discovery tests. Format verification passed. Standard publish and NSIS 3.12 `/WX` packaging passed.
- Actual Windows 11 x64 build 28000 lifecycle: first install to a previously nonexistent Chinese/space path, 462 file hashes checked, desktop shortcut target/icon confirmed; running-app upgrade rejected without closing it; closed-app upgrade succeeded in the same directory; uninstall removed owned binaries/registration/shortcuts and preserved a personal-file sentinel.
- Live installed integration window displayed the new icon and actual installation path. Status and invalid maintenance commands exited without opening windows. Binding writes are tested using isolated configuration; real smoke installation did not enable binding. Existing real global Diff, advanced DiffTools, backup-file hash and all five source-fixture hashes remain unchanged.
- Fixed a discovered first-install issue: NSIS GetFullPathName rejected nonexistent parent paths; replaced with GetFullPathNameW normalization and validation before directory creation. Final installer includes the fix.
- Package is **unsigned**. Win10 compatibility has not been tested on a clean Win10 VM or the user's company machine. No assurance is made about enterprise allowlist policy. Target users need their own TortoiseSVN for commit-window invocation, but no separate Office, .NET SDK/runtime or svn.exe.

SHA-256: `df7c3163ee5a055c4ca576cfaa30e7644ee514ccc9891f4f9f979bb183e120fe`. Full evidence: `artifacts/packaging/2026-09-25/交付记录.md` and `docs/INSTALLATION.md`.

## 0.14.2 — 2026-09-25: visual polish

Released at the fixed `artifacts/publish/Differ-win-x64/Differ.App.exe` path, version **0.14.2.0**. Development specification is **3.12.2**. The Excel comparison workflow, semantic model, source files, grid alignment, virtualization and character-level detail selection remain intact.

- Unified toolbar icon/button sizes, grouping, role-based radii and typography; navigation uses a single shallow surface.
- Softer light/dark semantic fills and grid lines, 3 DIP row markers and unchanged 2 DIP four-sided selection frames.
- Quieter minimap strokes and 25% hover expansion, preserving real preview and continuous drag navigation.
- Excel first-use details total height is 104 DIP (28 header + 76 content); persisted 72/124/180 values retain their total-height meaning. This machine's saved 180 DIP was verified unchanged.
- Fixed pre-existing horizontal-overflow tab clipping: file/sheet strips retain 30/28 DIP minimum heights and make room for the scrollbar only when necessary. Four actual-template regressions cover long labels and narrow windows.

Validation:

- `dotnet build -c Release`: passed, zero warnings/errors.
- `dotnet test -c Release`: **381 passed, zero failed**, including 63 Excel adapter and 166 App tests. Final TRX files are in `artifacts/design/2026-09-25-ui-polish/test-results/final*.trx`.
- `dotnet format --verify-no-changes --no-restore`: passed.
- Standard staging-first `scripts/publish.ps1`: passed. Publish root contains only `Differ-win-x64`, no ZIP. Published App DLL SHA-256: `3F9B8CB563B477B1BE92DE28EB34ADBFC7AB2DECF957D65AAF175663B7225E05`.
- Before/after real WPF rendering: 64 PNGs each, light/dark, 1440×900 and 980×680, 100/125/150/200% offscreen raster scaling, mixed added/deleted/modified cells, long Chinese content, formulas, collapsed details and multiple tabs. Both versions preserve 47 differences in the controlled fixture.
- Both baseline and candidate pass **119/121 probe checks**. The same two pre-existing edge cases remain: after narrowing to 980 DIP, selected E4 on the second sheet is partially outside the horizontal viewport. This does not represent a new regression; horizontal scrolling and complete details remain available. Do not report all probe checks as passed.
- Published executable was inspected live: light/dark switch, partial character selection in details, minimap drag to a different difference, synchronized dual selection and real hover preview. Native screenshots are `published-*.png` in the same artifact folder.
- All **5 original test-case file hashes unchanged**. Only isolated generated fixtures were used; no SVN configuration or write operation was performed.

Limits: offscreen raster scaling is not an OS per-monitor DPI transition test. Native clipboard contents, screen readers and a new large-workbook performance benchmark were not checked in this polish pass. No claim of complete accessibility or full native DPI QA is made.

Evidence and delivery summary: `artifacts/design/2026-09-25-ui-polish/交付记录.md`; detailed probe observations: `visual-probe-result.md` in the same folder.

## 0.14.1 — 2026-09-25: pressure workbook and cell selection

final result: passed

Scope: the user's actual 3-sheet / 12000-row workbook, compared against the BASE supplied by TortoiseSVN. Detailed measurements, raw JSON and source hashes are in `artifacts/performance/2026-09-24/fix-report.md` and its linked probe reports.

### Changes verified

- Selected spreadsheet cells now have a complete 2 DIP rectangle on both sides. The fixed overlay does not affect row height, text position, semantic background, mouse input or virtualization.
- Plain cells avoid creating rich text containers. Lightweight spreadsheet columns reuse a single cell presenter with one value binding instead of nested templates and six state bindings.
- Background preparation reuses only fully equal immutable values, stops saturated column measurement, avoids per-grapheme strings, and reduces address/metadata/node allocations. It does not omit rows, cells, unchanged context or diff nodes.
- Busy animation is tied to `IsBusy`. No claim is made that this resolves unrelated idle CPU observations; sampled accessibility-provider activity can affect such observations.

### Real input and equivalence

The real pair retains **997 differences**: 462 added, 481 deleted, 54 modified; worksheet counts 342 / 360 / 295. It retains all 504523 model nodes and aligned row counts 12023 / 12025 / 12020. Complete DiffDocument and complete presentation semantic digests match 0.14.0. The user's original working-file SHA-256 remains `5C3B1B46339607B67A03EE902E00B387FACDFB4EC18880869C9343394CFA05D3` after delivery.

### Controlled paired UI measurements

Same true BASE/MINE, window size and production WPF resources, using a screen-external probe and time to completed layout/dispatcher work. These are local measurements, not universal performance promises or screenshot-tool timings.

| Operation | 0.14.0 | 0.14.1 |
|---|---:|---:|
| Cold visit to the other worksheets | 740 / 776 ms | 431 / 449 ms |
| Cached worksheet switch p95 | 8.37 ms | 7.64 ms |
| Small vertical scroll p95 | 67.83 ms | 44.33 ms |
| Far vertical scroll p95 | 243.45 ms | 173.69 ms |
| Full-range horizontal jump p95 | 391.23 ms | 201.75 ms |
| Far cell selection p95 | 229.78 ms | 174.07 ms |

Nearby selection did not improve (median 3.15 → 4.19 ms). Rendering is improved but distant jumps are not instantaneous. Background allocations fall by approximately 30.5% (about 993 MiB per measured preparation); first parsing still takes seconds. The newly launched native staging run logged approximately 6.6 s comparison, 1.0 s presentation and 0.5 s first worksheet; do not describe this as instant loading.

### Tests and visible evidence

- Release build: zero warnings/errors. Full format verification passed.
- **370 tests passed, 0 failed, 0 skipped**, including 155 app tests and 63 Excel adapter tests. Final TRX set: `artifacts/performance/2026-09-24/passed-tests/`.
- Regression coverage includes full semantic equivalence, Unicode widths, shifted addresses, formula/cache details, rich/plain/empty recycling, both themes and four output DPI values for all four selection edges.
- The cached-switch benchmark now drains through ApplicationIdle during warm-up and measurement: Render alone stops before lower-priority Loaded callbacks and can count deferred cold column setup as cached switching. The original 100 ms p95 / 180 ms maximum budgets remain unchanged.
- Native 12000-row view visibly showed complete corresponding C121 rectangles and matching details. A later fresh capture showed complete A2591 rectangles after the view moved to a distant part of the table. No stale row-height or colored-background changes were observed.
- Native synthetic keyboard input was not completed because the window was actively receiving user input. Automated native cell selection/dual-pane synchronization and recycling passed; no manual arrow-key claim is made.
- Windows system DPI changes were not performed; four output-DPI render checks are separate from real system scaling.

### Delivery

- Fixed path updated to `E:\Differ\artifacts\publish\Differ-win-x64\Differ.App.exe`, version **0.14.1.0**.
- Published DLL SHA-256 matches the verified live staging DLL: `7681B397299CD928BAF64263224A5747496980D6B8D1B8CA833517CEB23C4584`.
- Old 0.14.0 retained at `artifacts/performance/2026-09-24/before/Published-0.14.0`.
- The user's active 0.14.1 preview in `artifacts/Differ-win-x64-staging` was preserved; publishing copied the identical build rather than closing or moving the active preview. The next normal start uses the fixed published path.
- The publish directory still contains only `Differ-win-x64`. No SVN configuration, source workbook or submission state was changed. The baseline diagnostic process opened for this task was ended before staging verification; source snapshots had already been preserved.

![Complete selected cell borders in the true pressure workbook](E:/Differ-归档/2026-10-24-cleanup/artifacts/performance/2026-09-24/selection-after-0141-light.jpg)

![Selection after moving far down the actual table](E:/Differ-归档/2026-10-24-cleanup/artifacts/performance/2026-09-24/selection-after-scrolled-0141.jpg)

---

## 0.14.0 — 2026-09-24

final result: passed

Scope: approved native WPF visual redesign, with existing diff functionality retained. This result covers the rendering and interaction checks below; Windows-wide switching between display scale factors was not performed.

### Visual target and comparison

- Approved plan: `artifacts/design/2026-09-24-ui-proposal/GDDiff-UI设计方案.md`.
- Original native Excel: `artifacts/design/2026-09-24-ui-proposal/03-current-excel-light.jpg`.
- Final native Excel: `artifacts/design/2026-09-24-ui-refresh/08-published-excel-light.jpg`.
- Original/final captures use the same supplied workbook pair, E9 selection, two file tabs, 1440×900 saved window geometry and 100% reading scale. Both Computer Use captures are 1427×894 pixels. These were opened together in the same comparison input; no screenshot content was repainted or reconstructed.
- ImageGen direction reference: `artifacts/design/2026-09-24-ui-refresh/visual-reference.png`. It is an art-direction aid, not a pixel-accurate contract: generated extra worksheet controls, altered sample text, header letters, type controls and decorative frames were deliberately not implemented. The approved specification and real data take precedence.
- Built-in ImageGen prompt and asset trace: `artifacts/design/2026-09-24-ui-refresh/visual-reference-prompt.md`.

### Findings and resolved iterations

- [P2, fixed] New detail header initially added 28 DIP to legacy saved heights. `DetailHeight` now retains its historical meaning of the whole expanded panel; saved 72/180 DIP values remain 72/180. New defaults are 124 overall, including 28 header and 96 content. Collapsing uses 28 without overwriting the saved height. Legacy preference and binding tests cover this.
- [P1, fixed] Word rows retained a previous horizontal extent after narrowing from 1500 to 980 DIP, clipping the current-side text. Rows now bind to the actual scroll viewport width while retaining virtualization. New tests enforce equal columns, no overflow, and alignment with the bottom side summary. The failure evidence is `test-results/layout-word-bounds.trx`; corrected narrow Word renders are in `artifacts/visual-validation/0.14.0/reading-docx-*-980x680-*.png`.
- [P2, fixed] Side headers repeated the current filename/display name. Text/Excel headers now show BASE / 工作副本 and an available revision only; complete original names, paths and revisions remain in tooltips.
- No unresolved P0/P1/P2 visual issue was observed in the inspected states. Native system caption color follows Windows and is an intentional platform constraint.

### Required visual surfaces

| Surface | Result |
|---|---|
| Typography | Text 13.5 DIP / 24 DIP rows, no decorative horizontal rules; Excel retains grid and alignment; Chinese and monospace fallback inspected in light/dark wide/narrow renders |
| Spacing | Toolbar 38, tabs 30, text/Excel header 28, minimap 44, status 24 DIP; layout rounding allows at most one physical pixel in numeric assertions |
| Colors | Neutral light/graphite dark; opaque reading surface; red/green/amber retained as diff semantics; selected states do not replace diff backgrounds |
| Assets | Existing native Windows icon font and vector/drawn UI; no generated bitmap used as interactive UI; minimap displays actual nearby content |
| Copy | Zero-count statistics omitted, comparison rule changes/pending state remain visible, warning entry retained, full-detail action named and accessible |

### Interaction checks

1. Published self-contained executable starts and two external comparison requests join one window — passed.
2. Text starts collapsed; expanding exposes BASE/current selectable detail documents, collapsing returns to the compact strip — passed in native UI and regression tests.
3. F8 changes 1/2 to 2/2 and updates the selected line and detail — passed in native UI.
4. Clicking the narrowed minimap returns to the first changed line and displays real two-sided preview content — passed in native UI (`05-minimap-preview.jpg`).
5. Switching text/Excel tabs retains the active comparison and expected detail state — passed in native UI; persistent file/worksheet view reuse also asserted automatically.
6. Light/dark toggle repaints the live Excel grid, minimap, toolbar and detail — passed; light appearance restored.
7. Esc exits the staging comparison, then the same tested build was installed at the stable path — passed; no comparison process remained before replacement.
8. Native installed process reports `Responding=True` and the verified publish path; final sample window remains open for review — passed.

### Verification and release

- `dotnet format Differ.sln --verify-no-changes --no-restore --verbosity quiet` — passed.
- `dotnet build Differ.sln -c Release --no-restore` — passed, zero warnings/errors.
- `dotnet test Differ.sln -c Release --no-build --no-restore` — **357 passed, 0 failed, 0 skipped**, including 142 application tests.
- TRX files: `artifacts/design/2026-09-24-ui-refresh/final-tests/`; aggregate output: `full-tests-final.log`.
- 46 render captures cover native WPF templates for code, Excel, Word and binary fallback, light/dark, wide/narrow, and 96/120/144/192 output DPI. `DPI_METHOD.md` explicitly distinguishes RenderTargetBitmap output DPI from real Windows display scaling and from reading zoom.
- Actual desktop interaction was checked at the current host display scale. Other native Windows scaling modes, high-contrast mode, remote desktop and a complete assistive-technology audit were not exercised.
- Version **0.14.0.0**, fixed executable `E:\Differ\artifacts\publish\Differ-win-x64\Differ.App.exe`.
- Published `Differ.App.dll` SHA-256: `261FFEF28E0910C67149993351902B4136F514510775E587A3D65239360C040D`, identical to the staging build exercised above.
- Prior published directory retained at `artifacts/design/2026-09-24-ui-refresh/before/Published-0.13.0`; original source, preferences and QA report also backed up under `before`.
- The publish root still contains only `Differ-win-x64`; no ZIP or parallel version directory was added there. Replacement used checked absolute paths and a rollback move instead of deleting the old version first.
- No SVN operation, registry write, source fixture edit, file-content algorithm change, cloud upload, file tree or history feature was introduced. Full SVN commit-dialog end-to-end interaction was not repeated; equivalent external arguments and the existing configured path were checked.
- Read-only registry verification confirms the global Diff command points to the fixed published executable. The 13 remaining advanced overrides are for other extensions (.docm/.dot/.dotx/.ods/.odt/.nb/.ppt/.pptm/.pptx/.sxw/.xlam/.xlsb/.zip); existing core text/Word/Excel bindings were not changed. Evidence: `svn-path-check.json` and `svn-overrides-check.json`.

### Evidence previews

![Published light Excel](E:/Differ-归档/2026-10-24-cleanup/artifacts/design/2026-09-24-ui-refresh/08-published-excel-light.jpg)

![Dark Excel](E:/Differ-归档/2026-10-24-cleanup/artifacts/design/2026-09-24-ui-refresh/07-final-excel-dark.jpg)

![Compact text](E:/Differ-归档/2026-10-24-cleanup/artifacts/design/2026-09-24-ui-refresh/02-final-text-collapsed.jpg)

---

## 0.13.0 current verification — 2026-09-19

Implemented the review-efficiency roadmap and reference-driven minimap update. The supplied 216×364, 3.92-second recording was inspected as visual evidence. Its narrow marks, local hover emphasis and floating preview were adapted to Differ's light/dark themes and real BASE/current content.

Release: zero warnings/errors; 340 tests passed, zero failed/skipped; format verification passed. Executable 0.13.0.0 is in the same fixed publish directory. The live SVN command still points there; no registry value was rewritten this turn.

Native checks on disposable inputs:

1. Minimap — passed: light/dark real previews, local enlargement, centered navigation and viewport tracking. Unchanged selection retains the total difference count.
2. Search — passed: Ctrl+F and Hero50 navigation to original line 53; local Escape leaves the comparison window open.
3. Context — passed: three surrounding lines, original line numbers, expandable blocks. The ordinary button minimum was overridden to prevent folded-row text clipping, with a real template regression.
4. Key alignment — passed: shuffled CSV records produced 12 default differences; key A reduced them to the single value change, preserving both row numbers.
5. Refresh — passed: F5 changed working value 120 to 125 while BASE stayed 100 and its hash was unchanged.
6. Multiple files/cleanup — passed: a second external request joined the existing window and exited 0; closing test windows left zero session snapshots.

Automated coverage also includes cross-sheet Go To, invalid-key fallback, pending/applied presets, atomic settings, export encoding/protected paths, refresh rollback/cancellation, zoom, local keyboard scopes, minimap fold-expansion reentrancy and bounded hover processing. Reports describe applied rules rather than pending menu edits.

Real WPF tests cover 980-DIP layout and 16 simulated 100/125/150/200% raster outputs. This is not a full native multi-monitor DPI/accessibility audit. UI latency tests are isolated from concurrent DPI rendering; the existing 80/100 ms p95 and 150/180 ms maximum thresholds were not relaxed.

Evidence: [delivery and usage](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/2026-09-19-0.13.0-delivery.md), [verification](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/0.13.0-verification.json), [screenshots](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.13.0).

The sections below are historical records.

## 0.12.0 current verification — 2026-09-19

Release build and format verification passed with zero warnings/errors. All 263 tests passed (153 before this work). The fixed self-contained publish directory contains 0.12.0.0.

Native Windows checks used disposable inputs and an isolated copy of the SVN working-copy metadata. The real TortoiseSVN commit dialog launched Differ when its README row was double-clicked. A subsequent TortoiseSVN comparison opened another tab in the same comparison window. No commit, update, revert, add or delete operation was performed.

1. Integration page: healthy. Installed the fixed executable through the normal backed-up integration service. The live global Diff command resolves, all supported extension overrides are empty, and original settings have recovery backups.
2. Excel review: healthy. Real aligned grids, per-cell navigation, worksheet switching and hidden-column details were inspected. Light/dark theme button switching remained readable. The final hidden-column detail uses Excel letters rather than internal numeric ranges.
3. Word review: healthy within the logical-document scope. Native rendering showed the changed content-control text inside a table and the changed body paragraph.
4. Binary fallback: healthy. A corrupt workbook selected the text/hex template on the same view model and showed the changed byte plus a compact warning.
5. Comparison options: healthy. The native menu exposes relevant options, retains check states, and applies a new comparison without replacing the file tab. Automated tests cover cancellation, stale results, original text preservation and ignored inline changes.
6. Closing: healthy. Ctrl+W reduced five snapshot directories to four; closing the comparison window left zero session snapshots. The SVN commit selection remained unchanged, then the dialog was cancelled.

Evidence:

- [Machine-readable release verification](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/0.12.0-verification.json)
- [Performance measurements from published DLLs](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/0.12.0-performance.json)
- [Repair details and recommendations](E:/Differ-归档/2026-10-24-cleanup/artifacts/audits/2026-09-19-fixes-and-roadmap.md)
- [Native screenshots and simulated DPI output](E:/Differ-归档/2026-10-24-cleanup/artifacts/visual-validation/0.12.0)

Eight real WPF runtime cases cover xlsx/docx, light/dark, and both truncated ZIP and non-ZIP payloads. They verify template replacement, resource inheritance and a 980-DIP toolbar with visible, non-overlapping controls. Sixteen RenderTargetBitmap outputs cover 100/125/150/200% output DPI. These are simulated DPI renderings, not a claim that Windows per-monitor scaling was changed or completely tested. Keyboard/screen-reader accessibility has not received a full compliance audit.

The earlier 0.8.0 record below remains historical evidence. Temporary SVN verification-copy cleanup was rejected by automatic approval review ("blocked by policy"), so that disposable copy remains in the temporary directory; application session snapshots were successfully cleaned by the app itself.

## Historical 0.8.0 record

## Current scope

This record covers Differ 0.8.0 theme completion and comparison-window
keyboard access. The previous 0.5.0 screenshots under
`artifacts/visual-validation` remain historical evidence only and are not used
to claim that the current build passed.

Validation date: 2026-08-14.

## Automated guards

- Light and dark theme dictionaries load through the same WPF pack URI used by
  production.
- Both dictionaries define exactly the same resource keys.
- Every production `DynamicResource` and code-side theme-key reference exists
  in both dictionaries.
- Production XAML outside the theme dictionaries contains no literal UI color.
- Production C# controls do not construct literal color brushes.
- Theme services use independent resource dictionaries and remain isolated when
  tests run concurrently.
- System preference changes and manual toggles replace one existing dictionary
  instead of stacking resources.
- The shortcut resolver covers F7, F8, F9, Ctrl+U, Alt+Left, Alt+Right, Ctrl+W,
  and Escape without intercepting copy or modified variants.

## Native WPF checks

The Release build was opened directly and checked in its real WPF windows:

- The no-argument TortoiseSVN integration page opened in the Windows-selected
  light theme with a visible and accessible theme button.
- Switching that page to dark mode updated cards, banners, fields, buttons,
  labels, and ordinary inherited text. A low-contrast inherited heading found
  during this pass was corrected with a dynamic implicit text style and then
  rechecked.
- A text comparison opened with aligned rows, line numbers, inline changes,
  detail panes, minimap, file tab, and toolbar intact in both themes.
- `Ctrl+U` hid unchanged rows, `F8` moved the counter from difference 1/2 to
  2/2, and `Alt+Left` / `Alt+Right` moved keyboard focus to `BaseTextGrid` /
  `MineTextGrid`.
- An Excel comparison opened in dark mode with worksheet grid context, row and
  column headers, selected-cell detail, worksheet tabs, inline change emphasis,
  and synchronized two-pane layout readable.
- A Word document opened in dark mode and switched back to light without
  recreating the tab; body text, headings, table/document surfaces, minimap,
  separators, and BASE/current footer summaries all updated.
- Theme changes did not alter row height, pane width, padding, or tab layout.

## Repository and release checks

- The temporary theme diagnostic writer was removed; the build no longer
  creates `theme-diagnostics.log`.
- Three unused DiffWindow styles and the unused literal-color converter were
  removed.
- Real-machine TortoiseSVN backup artifacts were removed from version control;
  runtime backups continue to live outside the repository through the existing
  backup store.
- Word and Excel formatting-only changes are ignored by default and detected
  only when formatting comparison is explicitly enabled; tests modify copies,
  never files under `测试用例`.
- The fixed publish destination remains
  `artifacts/publish/Differ-win-x64` with no ZIP or parallel version directory.

## Result

Passed for the 0.8.0 theme, keyboard, Office-default, and repository-hygiene
scope. Historical 0.5.0 TortoiseSVN registry screenshots are not treated as a
fresh integration reconfiguration test; this pass did not modify the user's
current TortoiseSVN settings.
