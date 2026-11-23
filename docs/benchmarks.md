# RuleOpen Performance Benchmarks

All measurements below were captured directly using [`scripts/run-benchmarks.ps1`](../scripts/run-benchmarks.ps1) against Release x64 builds on a real Windows 11 environment.

## Test Environment

| Component | Specification |
| --- | --- |
| **OS** | Windows 11 Home 25H2 (Build `26200.9974`) |
| **CPU** | 11th Gen Intel Core i7-1165G7 @ 2.80 GHz (4 cores / 8 logical processors) |
| **RAM** | 15.8 GB |
| **Runtime / SDK** | .NET SDK `10.0.402` / Runtime `10.0.13` / Windows App SDK `1.8.260921001` |

## 1. Pure Rule Engine Throughput (`RuleEngine.Evaluate`)

Measured in-process across 10,000 iterations per tier after a 500-iteration JIT warmup:

| Rule Count | Mean Evaluation Latency |
| --- | --- |
| **10 rules** | **50.91 µs** (0.05 ms) |
| **100 rules** | **155.97 µs** (0.16 ms) |
| **500 rules** | **603.52 µs** (0.60 ms) |

Rule evaluation stays sub-millisecond even with 500 configured rules and zero caching across invocations.

## 2. Lightweight Dispatcher vs. WinUI Management UI

Measured end-to-end on the self-contained x64 staged release (`-UseStaged`, `RuleOpen.Dispatcher.exe` published with `PublishReadyToRun=true`):

| Metric | `RuleOpen.Dispatcher.exe` (Zero WinUI) | `RuleOpen.Desktop.exe` (WinUI 3 Management App) |
| --- | --- | --- |
| **Cold startup / dispatch** | **281.5 ms** (full process start → rule match → child launch → exit) | **877.6 ms** (time to visible window) |
| **Warm median / run 2** | **248.4 ms** | **749.5 ms** |
| **Working set memory** | **29.2 MB** peak | **132.1 – 132.7 MB** |

### Why This Matters
Double-clicking a file in File Explorer invokes `RuleOpen.Dispatcher.exe`, avoiding Windows App SDK / WinUI 3 XAML initialization completely — saving over **100 MB of memory** and **~500–600 ms** on every file activation.

*(Framework-dependent non-ReadyToRun builds from `bin/x64/Release` measured `347.3 ms` cold, `277.8 ms` warm median, `30.2 MB` peak for Dispatcher; `1117.0 ms` / `908.5 ms` and `136.2–137.3 MB` for Desktop.)*
