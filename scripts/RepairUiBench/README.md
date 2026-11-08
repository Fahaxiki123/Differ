# Windows WPF interaction benchmark

Uses the same fixed inputs produced by `scripts/RepairBench`: Excel 10,000 × 40, Word 1,000 paragraphs, and five Excel sheets of 1,000 × 30. Does not generate or modify these inputs. Run baseline and current sequentially, with no other benchmarks or builds competing for CPU.

```powershell
$archive = 'E:\Differ-归档\2026-10-24-cleanup\artifacts\repair-2026-10-24'
$benchmarkOutput = Join-Path $env:LOCALAPPDATA 'Differ\WorkspaceMaintenance\ui-benchmark'
New-Item -ItemType Directory -Path $benchmarkOutput -Force | Out-Null
dotnet run --project scripts/RepairUiBench -c Release "-p:SourceRoot=$archive\baseline-source" -- "$archive\samples" "$benchmarkOutput\ui-before.json"
dotnet run --project scripts/RepairUiBench -c Release -p:SourceRoot=E:\Differ -- "$archive\samples" "$benchmarkOutput\ui-after.json"
```

Historical evidence was moved outside the project during the 2026-10-24 cleanup. Adjust `$archive` to the actual restored archive location on another machine. Keep new measurements outside the source tree; use a separate output directory for each measurement that must be retained. After verification, archive useful reports and run the repository cleanup/check commands required by `AGENTS.md`.

SourceRoot is compiled into the harness and controls both the project reference and the production App.xaml resources. The `Differ.App.Tests` assembly name uses the existing friend assembly solely to inspect the actual production view models. No test doubles replace the views, adapters, scheduling or selection logic. Close any source-tree build of Differ before building; published builds can remain separate, but close them for a quiet benchmark environment.

After five warm-up actions, each operation collects 100 latency samples. Ordinary actions are `excel-medium.small-scroll` / `word-medium.small-scroll` (three LineDown calls) and `excel-medium.visible-cell` (an actually realized business cell whose center is inside the viewport). Stress navigation is separately named `excel-medium.scrollbar-jump` / `word-medium.scrollbar-jump` (distant jumps through 37% steps), and `excel-medium.remote-cell` (row index increments of 97 plus horizontal navigation). Other operations are `word-medium.next-region`, `excel-multi.sheet-switch`, and `loaded-file.switch`. All retain the same budgets; distant-navigation failures are still reported, never hidden by the ordinary-action results. The JSON retains every sample plus p50/p95/max, managed allocations, GC counts, working set, file hashes and first operable layout times. A 16.67 ms Input-priority DispatcherTimer records UI heartbeat gaps during loading and each operation.

The budget is fixed before measurement: p95 ≤ 100 ms for common operations; 16.67 ms is the 60 Hz layout budget; heartbeat excess delay > 200 ms is separately counted. Missing targets remain marked false. A screenshot cannot establish performance.

Default windows are offscreen and inactive; `--visible` places them onscreen. These metrics end after WPF ApplicationIdle/layout, and **do not establish physical display frame rate or real mouse, keyboard, IME event latency**. Report that limit even when all layout budgets pass. Code editor input is intentionally outside this harness. For process-start variance, repeat each command in a fresh process and aggregate across runs; first load in one process is not an OS cold-cache measurement.

Use `--operation=excel-medium.remote-cell --events=10` for a small targeted diagnosis (the delivery run uses 100). Every action start/completion is appended immediately to `<output>.samples.jsonl`. A background-thread watchdog terminates only the owned harness process with code 124 if an action/dispatcher drain exceeds 20 seconds; `<output>.watchdog.json` identifies the stalled phase/event, and prior samples remain available. A timeout is a failed measurement, not a missing sample to discard. Business-cell selection excludes the row-number gutter at DataGrid column zero.
