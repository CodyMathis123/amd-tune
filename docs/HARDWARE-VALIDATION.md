# RX 9070 XT hardware validation runbook

This checklist is directly executable on a Windows machine and must be performed **manually**. It cannot be substituted by simulator CI.

## Gate 0 — prerequisite checks (no writes)

1. Ensure GPU clocks/voltages are at known Adrenalin defaults. Export a separate backup of your current tuning profile.
2. Confirm the native bridge was compiled from AMD's official ADLX SDK at the pinned revision. The CI-generated complete development bundle works without Visual Studio Build Tools; the local source build requires the MSVC x64 C++ toolchain. .NET 10 is required for local managed builds.
3. Build and run:

       ./scripts/build-native.ps1 -AdlxSdkDirectory ./third_party/ADLX
       ./scripts/publish-windows.ps1
       ./scripts/hardware-doctor.ps1 | Tee-Object -FilePath artifacts/hardware-doctor.log

4. Confirm the name identifies **your RX 9070 XT**, not an iGPU or different adapter. Confirm driver-reported VoltageOffsetMv/MemoryClockMhz ranges and explicit Fast Timing support. Confirm hotspot is plausible.
5. Launch in read-only mode. Check that there are no tuning changes after navigation or refresh:

       ./artifacts/app/AmdTune.Web.exe --hardware

## Observed read-only RX 9070 XT results — November 15, 2026

First direct hardware report, based on an operator-run, read-only hardware doctor (not CI):

| Metric | Observed |
| --- | --- |
| GPU name | AMD Radeon RX 9070 XT |
| ADLX | 2.0 native bridge; one discrete GPU |
| Voltage offset range | -200 to 0 mV; step 1 mV |
| VRAM frequency range | 2518 to 3000 MHz; step 2 MHz |
| Memory timings adjustable | Yes (driver capability discovery) |
| Current configured offset / VRAM limit | 0 mV / 2518 MHz |
| Idle hotspot / board power | 26°C / 10 W |
| Idle instant clocks | Core 0 MHz / memory 1 MHz (not a configured clock limit) |
| Driver settings readback | Successful |
| GPU writes exercised | None |

Interpretation: native ADLX discovery, capability ranges, configured settings readback, and idle telemetry work on this machine. These are device-reported ranges, **not safe operating limits or a verified overclock**. Instantaneous idle clocks may report zero or near-zero; readings under load have not been tested.

The initial browser launch returned HTTP 404 for `/` because the host assumed the current working directory contained `wwwroot`. This is separate from successful ADLX discovery. The host has been fixed to resolve packaged UI assets relative to the application; CI now smoke-tests `/`, `/app.js`, and `/styles.css` from an unrelated working directory. **Gate 0 browser validation must still be repeated after the fixed build is installed.**

## Gate 0B — Independent recovery process startup (no GPU writes)

An actual RX 9070 XT operator run produced a failed experiment report (2026-11-15):
`SafetyException: Recovery process did not acknowledge readiness`, with zero trials and rollback unverified. This is an early safety stop: the experiment did not reach a tuning write. Root-cause code review identified the development bundle copying only the framework-dependent recovery .exe without its runtime/dependency files.

**Fix:** publish the recovery guard as a framework-dependent single-file .NET executable and test startup from the final bundled location. Gate 0B must pass before any further experimental hardware writes.

The package CI now runs the recovery executable's no-write `--self-check` and a watchdog lease/disarm handshake with its actual process. After installing the fixed bundle locally, run:

    ./artifacts/app/AmdTune.Recovery.exe --self-check
    ./scripts/smoke-recovery.ps1 -AppDirectory artifacts/app

Expected: `RECOVERY_RUNTIME_OK` and `RECOVERY_HANDSHAKE_PASS`. The handshake starts a recovery process with the parent alive, checks the readiness file, then disarms it; it does not intentionally call the AMD bridge or modify the GPU. Do not forcibly kill the parent during this diagnostic.

## Gate 0C — Watchdog heartbeat and bounded benchmark progress

The operator's independent watchdog startup/disarm test **passed on actual Windows hardware** (2026-11-15):

- Local publish and recovery executable `--self-check`: `RECOVERY_RUNTIME_OK`
- Real independent recovery process no-write handshake: `RECOVERY_HANDSHAKE_PASS`
- Read-only driver state afterward: 0 mV, 2518 MHz, default timing, 25°C hotspot, 10 W board power

Code review found that the original watchdog lease was only refreshed *between* workloads, while the recovery monitor considered a lease stale after 20 seconds. A sufficiently slow valid DX12 test could therefore trigger an unintended rollback mid-benchmark.

The engine now has a **separate two-second heartbeat** independent of benchmark execution, a **90-second per-workload cancellation budget**, and a **120-second forward-progress deadline**. A stalled experiment eventually stops heartbeating, allowing the watchdog to restore the original settings after its 20-second lease timeout. These are engineering safety bounds, not assertions that GPU hangs are always recoverable.

CI tests the heartbeat timer independently with injected failures. Physical GPU workload and rollback behavior remain unverified and must be observed during Gate 1.

## Gate 0D — Read-only DirectX 12 workload verification

An early physical experiment produced **zero completed trials** with a reported
`ArgumentOutOfRangeException (groupsX = 524288)` in the 128 MiB memory workload.
The engine subsequently verified rollback, and ADLX discovery confirmed the original
0 mV/2518 MHz/default-timing settings. The older JSON report alone does not prove
whether a candidate write was attempted; consult the corresponding `.events.jsonl`.

**Cause:** ComputeSharp's default 64-thread X group size produced 524,288 groups
for a 33,554,432-element test. The standard DirectX 12 per-dimension dispatch limit
is 65,535 groups. Source: Microsoft [D3D12 Dispatch documentation](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-dispatch).

**Fix:** split the memory workload into 1,048,576-element dispatches
(16,384 groups each), passing an absolute base offset to the shader so all
33,554,432 results are still independently verified. CI covers slice boundaries,
full coverage, and the 128 MiB case without physical hardware.

**Required next check, NO tuning writes:**

    git pull --ff-only
    ./scripts/publish-windows.ps1
    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu
    ./scripts/hardware-doctor.ps1

Expected: `GPU_VERIFY_PASS` with independent CPU/GPU correctness for Compute,
Memory, and Mixed workloads; initial and final settings identical. This diagnostic
does **not** call `ApplyAsync` or start a tuning session. It still executes GPU
workloads and thus may trigger driver instability on faulty hardware.

If this does not pass, do not start a tuning experiment. Share the console output
and audit log. Only attempt Gate 1 after this read-only check passes.

## Gate 0D — Physical DirectX workload verification: PASS (November 15, 2026)

The operator ran `AmdTune.Web.exe --hardware --verify-gpu` on a physical AMD Radeon RX 9070 XT, with these directly observed results:

| Workload | Elements checked | Dispatches | Mismatches | Elapsed, end-to-end |
| --- | ---: | ---: | ---: | ---: |
| Compute (64 rounds) | 1,048,576 | 1 | 0 | 26.4 ms |
| Memory (1 round) | 33,554,432 | 32 | 0 | 68.2 ms |
| Mixed (16 rounds) | 4,194,304 | 4 | 0 | 9.7 ms |

All three reported `ExecutedOnGpu=true`, `Correct=true`, and the diagnostic printed `GPU_VERIFY_PASS`. The selected DX12 adapter matched `AMD Radeon RX 9070 XT`. Initial and final driver settings were identical: 0 mV voltage offset, 2518 MHz VRAM limit, default memory timing. GPU execution and oracle checks are **verified on hardware**, but the timing samples are one-off observations and do not imply a stable overclock or a calibrated memory-bandwidth metric.

### Additional prewrite safety

Review identified that an experiment failing during the stock baseline path would still call `ApplyAsync(original)` in `finally`, unnecessarily writing driver settings despite having applied no candidate. The engine now records whether any candidate write was **attempted**; baseline-only failures perform readback-only verification, and candidate attempts always retain the full rollback path. CI tests this with an injected high-hotspot baseline failure and a counting hardware mock.

**Next physical gate:** exactly one manually entered -15 mV candidate; VRAM increase 0 MHz; original timing; 3 repetitions. Inspect the JSON report and matching `.events.jsonl`. No guided sweep or persistent profile yet.

## Gate 1 — conservative first write (will modify hardware)

Only proceed after verifying Gate 0. Stop other tuning tools (Adrenalin's manual profile editor, MSI Afterburner, etc.). Save important work. Real GPU instability may reset the display driver or system.

1. Start the exact command:

       ./artifacts/app/AmdTune.Web.exe --hardware --enable-hardware-writes

2. In the UI: core offset -15 mV relative to current; VRAM +0 MHz; timing unchanged; repetitions=3.
3. Verify the recovery process starts **before** ADLX applies a candidate, status shows a matching driver readback, all tests run, and original voltage/VRAM/timing are restored.
4. Independently confirm original values in AMD Adrenalin after the test. Check the JSON audit in %LOCALAPPDATA%/AmdTune/reports.
5. Use a separate monitoring tool to compare GPU clocks, hotspot and board power.

## Optional memory-sweep investigation

Only AFTER the initial -15 mV test, rollback, and recovery fault-injection have passed, return to stock voltage, choose **Preview conservative VRAM sweep**, and review the +25/+50/+75 MHz candidates. These are small hardware overclocks, not risk-free. The app must restore stock after each candidate and report performance changes, noise and correctness; a performance knee must **never** be called proven ECC activity. Do not combine a VRAM sweep with a core undervolt in the first hardware session.

## Gate 1 — First physical -15 mV trial completed (November 15, 2026)

User-supplied report ID `27090c55-1ae9-4a37-9785-5532c2a7b914` from Windows RX 9070 XT:

- Lifecycle phase **Completed** (7), trial verdict **Inconclusive** (2), not Rejected.
- Candidate: -15 mV offset; VRAM 2518 MHz; Default timings. One candidate; A/B/A repeated three times per phase; ~19 seconds end-to-end.
- Candidate workload correctness: nine GPU-executed passes, every output correct, zero mismatches. Not a long-term stability claim.
- `RollbackVerified=true` and `Error=null`: original settings restored and driver readback verified by the main process. Separate recovery *after parent termination* remains unverified.
- Candidate telemetry recorded hotspot 24 C, board power 13 W, idle-like core clock 0 MHz. This is not useful sustained loaded power/temperature evidence.

| Workload | Before stock mean (million checked elements/s) | Candidate -15 mV mean | After stock mean | Stock before-to-after drift | Candidate CV |
| --- | ---: | ---: | ---: | ---: | ---: |
| Compute | 193.63 | 184.44 | 182.58 | -5.71% | 2.46% |
| Memory | 529.87 | 493.41 | 394.09 | **-25.62%** | **11.02%** |
| Mixed | 340.82 | 378.36 | 347.35 | +1.91% | 7.27% |

**Result:** First actual nonpersistent -15 mV trial executed, checked outputs, and restored original settings. The reported before/after *stock* drift alone is too large to infer a tuning benefit. Workloads were measured over just 5–77 ms including transfers; these are not calibrated GPU-only bandwidth or full-load efficiency metrics.

**Next method improvements before optimization:** warmup, longer sustained work, isolate GPU-only timing from readback/transfer overhead, sample power/clocks/temperature during GPU activity, and treat correctness, stability, and performance confidence as separate outcomes. Increasing repetitions from 3 to 7 improves the sample count but cannot eliminate systematic drift from timed sequence or cold/warm state. Do not sweep voltage or VRAM on the current performance evidence.

## Extended benchmark profile — next validation gate

The operator also supplied report `9b6761c9-e3dd-475c-ba94-47061b3f96ef`
with seven repetitions at -15 mV, completed with zero mismatches,
`RollbackVerified=true`, and an inconclusive performance verdict. Relative to
the 3-repetition run, stock-to-stock memory drift fell from -25.62% to +3.81%,
but compute stock-before CV reached 29.23% and mixed candidate CV 13.27%.
Increasing repetition count alone did not establish repeatable performance gain.

The opt-in `--extended-benchmark` profile uses two untimed warmup passes and
16/12/16 measured passes for Compute/Memory/Mixed. The existing memory dispatch
partition is kept; outputs are compared to a CPU reference after **all** GPU
passes. It provides more work per measurement, but it still does not offer
GPU-only timestamp queries, power-efficiency measurements, or minutes-long
stability testing. The app continues to restore stock after experiments.

**First test (read-only):**

    git pull --ff-only
    ./scripts/publish-windows.ps1
    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --extended-benchmark
    ./scripts/hardware-doctor.ps1

**Only if the read-only check passes**, optionally run one -15 mV experiment
using `--hardware --enable-hardware-writes --extended-benchmark`, with 3 UI
repetitions, 0 MHz memory increase, and unchanged timings. Confirm
`RollbackVerified=true` afterward. Do not interpret quick versus extended
raw throughput numbers as a valid A/B comparison.

## Measurement research and sustained profile

Research and explicit next steps are in
[Measurement Methodology](MEASUREMENT-RESEARCH.md). The old quick/extended
timings of 4–73 ms were not sufficient to infer GPU performance improvements,
and idle post-load power readings were not valid loaded-power observations.

With the new version, **run read-only first**, including one bounded
60-second repeated-correctness soak. Check original ADLX settings after both
steps. If correctness passes and sampling remains healthy, a single -15 mV
sustained-profile trial is the next write safety test. Automatic tuning stays
disabled pending real failure-injection, longer stability and calibrated GPU
timing.

## Sustained physical read-only verification: PASS (November 15, 2026)

The RX 9070 XT operator validated source commit `8bb1a333` with a recent ADLX bridge. **Current settings were a custom profile** of `-50 mV` voltage offset, `2700 MHz` configured VRAM limit and `Fast` timings. Initial and final ADLX settings matched; no candidate tune was applied.

| Workload | Measured window | Passes | Checked outputs | Mismatches | ADLX active/total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Compute | 753.5 ms | 6,874 | 1,048,576 | 0 | 1/2 |
| Memory | 831.6 ms | 268 | 33,554,432 | 0 | 1/2 |
| Mixed | 759.9 ms | 1,895 | 4,194,304 | 0 | 1/1 |

The command printed `GPU_VERIFY_PASS`. Concurrent sensor shutdown did not spuriously fail Mixed. `hardware-doctor.ps1` printed `Native bridge GPU utilization telemetry schema: True`, and returned idle utilization 0% before and after validation. **Do not treat** the one loaded board-power reading per workload (51 W compute, 54 W memory, 40 W mixed) as a stable power or performance-per-watt estimate; the number and sampling cadence are insufficient. Host-timed synthetic elements/s is not GPU timestamp timing, VRAM bandwidth or a gaming benchmark.

The operator initially attempted to extract the current native bridge into a directory containing the same filename; `gh run download` reported `file exists`. This did not compromise the read-only validation: the bridge actually invoked does advertise the optional utilization field. A failed download does **not** establish byte-for-byte binary provenance; to require exact build verification compare SHA-256 hashes with the current CI artifact.

**Next gate, READ-ONLY, with the same known custom profile:**

    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark --soak-seconds 60
    ./scripts/hardware-doctor.ps1

Expected: `GPU_SOAK_PASS`, zero mismatches, unchanged driver settings, a new audit under `%LOCALAPPDATA%/AmdTune/soaks`. It runs repeated ~750ms host-timed GPU work with CPU verification breaks and is **not** 60 seconds of uninterrupted graphics load. Before meaningful stock-vs-overclock experiments, save the custom profile and restore factory defaults manually in Adrenalin, verify readback, then perform independently comparable runs. No write-enabled sweep, auto-tuning or fault injection yet.

## Read-only 60-second sustained correctness soak: PASS (November 15, 2026)

Operator-supplied audit ID `af209055-a5b3-469d-91fa-f179bd739238`, actual RX 9070 XT, `Simulated=false`; started 22:39:48.589 UTC and finished 22:40:49.874 UTC (61.284 seconds wall time). `Completed=true`, `Error=null`. **No tuning writes**: initial and final settings were exactly `-50 mV / 2700 MHz VRAM / Fast Timing`, which is **a custom profile, not factory stock**.

- 20 Compute, 20 Memory, 20 Mixed measurements, all 60 `ExecutedOnGpu=true`, `Correct=true`, `Mismatches=0`.
- 46.457 total seconds of host-timed dispatch+readback windows within 61.284 wall seconds. This is repeated correctness validation **with CPU checking between windows**, not 60 seconds of uninterrupted GPU work.
- Sample mean synthetic throughput in billions of processed elements/s: Compute **9.747** (CV **1.71%**); Memory **10.963** (CV **4.58%**); Mixed **11.725** (CV **4.77%**). These are not GPU-only elapsed times, memory bandwidth, game frame rates, or performance-per-watt.
- First 10 vs final 10 mean score changes: Compute **-1.01%**; Memory **+4.05%**; Mixed **+5.81%**. Although all CVs are under the preliminary 5% threshold, this temporal trend is compatible with warmup, caching, GPU state changes or scheduling effects. No causal explanation has yet been established.
- The audit contains **81** asynchronous ADLX samples, including **69** meeting the provisional `GPUUsage >= 50%` and positive GPU clock criterion. **Zero of 60** individual measurements met `HasUsefulLoadEvidence` (minimum 3 active samples each); do not use the incidental readings to calculate energy efficiency or to approve tuning. Highest sampled hotspot was **26 °C**; this short/light synthetic workload does not demonstrate a hot-state thermal limit.
- No OS crash, sensor-stop failure, unexpected tuning state change, or GPU correctness mismatch is recorded **in this session**. This does not establish general stability at -50mV/2700MHz/Fast timings, nor independently test killed-process rollback while an active candidate tune was applied.

**Next instrumentation targets:** retain clean no-write correctness soak as a regression gate; sample sensor metrics persistently with less process startup overhead and trustworthy timestamps; incorporate GPU command-queue timestamp queries; warm up and separately compare cold/warm sessions; extend heavy-load testing to hot steady-state with active thermal guards; and require at least two independent stock reference sessions before any performance-based recommendation. Automatic changes and persistent profiles remain disabled.

## Stock read-only monitoring diagnostic and next paired experiment

The operator saved/disabled the custom Adrenalin profile. Two read-only
runs at **0 mV / 2518 MHz / Default timings** each passed all three GPU
workloads with zero mismatches, and initial/final driver settings matched.
At a 125ms request, distinct ADLX samples per workload were 6/7/5 with
4/6/5 active readings. At 500ms, 2/2/2 distinct and active readings.
The synthetic Mixed score difference (+18.49% for 500ms vs 125ms) has
only one observation per condition and cannot establish monitoring overhead.
More detailed analysis is in [Measurement Methodology](MEASUREMENT-RESEARCH.md).

New read-only comparison command after successful CI validation:

    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark --compare-telemetry

The command runs two ABBA/BAAB crossover rounds **per workload** and
checks all results against the CPU oracle, with sampled thermal guards and
final driver readback. No writes or automatic tuning. Report stored under
`%LOCALAPPDATA%/AmdTune/soaks` with suffix `.comparison.json`.
Only run this diagnostic when the system is otherwise idle and save work
first, because real GPU workloads are executed.


## Physical telemetry comparison: PASS, but sampling artifacts remain (November 15, 2026)

Operator ran stock RX 9070 XT at driver settings `0 mV / 2518 MHz VRAM / Default timings`. Read-only ABBA/BAAB telemetry crossover finished `GPU_MONITOR_COMPARE_PASS` for **all 24 measurements**, with zero detected GPU mismatches. Original and final ADLX tuning readback matched. Audit: `b28362ca9abe42f5b68167085092757a.comparison.json`.

| Workload | 125ms score (mean, synthetic elements/s) | 125ms CV | 500ms score (mean) | 500ms CV | 500-vs-125 |
|---|---:|---:|---:|---:|---:|
| Compute | 11.975 billion/s | 1.21% | 11.349 billion/s | 4.96% | -5.23% |
| Memory | 11.681 billion/s | 3.04% | 11.707 billion/s | 2.07% | +0.22% |
| Mixed | 13.559 billion/s | 4.49% | 13.612 billion/s | 4.96% | +0.38% |

125ms sampling produced 21/24/21 distinct ADLX observations across four runs in each kind, versus 8/8/8 at 500ms. Memory and Mixed show almost no average difference, while Compute appears faster *with more frequent* polling. **Do not infer causality**: only four observations per condition, synthetic *host* timing, and nonstationary utilization (Compute observed ADLX means from 11% through 100%).

The source review found a concrete evidence-contamination path: the sampler is started before `SustainedGpuBenchmarkRunner.RunAsync` performs ADLX device discovery, CPU reference calculations, 128MiB allocations, shader warmup, and final CPU correctness checks. Therefore, even correct telemetry observations may come from **outside the actual timed GPU dispatch/readback interval**, artificially depressing average utilization and corrupting performance-per-watt inference.

This iteration adds `HostTimedWindow` start/end timestamps captured directly around the sustained runner's timed dispatch plus GPU buffer readback (not native GPU queue timestamps). The telemetry collector continues to observe and enforce hotspot safety across **all** readings, retaining every snapshot in the raw audit, but uses only readings falling inside the host-timed interval for exploratory active-load power/clock summaries. It records total observations and in-window observations separately. Tests cover time-window filtering and invalid bounds. This change does not enable automated tuning, prove efficiency, or eliminate ADLX internal averaging/latency. Proper D3D12 GPU timestamp query implementation remains the next timing milestone.

## Next milestone: full five-minute read-only study

The short 24-measurement stock crossover passed all correctness checks,
but Compute had a -5.23% 500ms-vs-125ms host throughput difference
while Memory/Mixed each differed by less than 0.4%. The sample count is
insufficient for performance causality or thermal conclusions.

The next **five-minute read-only experiment** performs rotating
nine-workload-condition blocks with 125/250/500ms telemetry cadences,
preserving raw ADLX traces and per-block GPU scores. It excludes the
first block as an unscored warmup, records full session/host-GPU time
and early/late drift, and aborts on any sensor/thermal/correctness failure
or changed tuning readback. It does not write settings.

    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark --study-seconds 300

Expected `GPU_STUDY_PASS`; report under
`%LOCALAPPDATA%/AmdTune/soaks/*.study.json`. **No guarantee of a
continuous 300-second GPU load** because CPU verification occurs between
shader windows. Do not promote to write-enabled automation based only on
a passing soak. Continue to require a real crash-recovery fault test
before automatic tuning.

## Gate 2 — recovery fault injection

Do this only after Gate 1 passes, with an unmodified stock profile available.

1. Start a second conservative test.
2. While the hardware candidate is active, forcibly terminate **only the AmdTune.Web.exe process** in Task Manager, not the Recovery process.
3. Within 20 seconds, the independent watchdog should notice the dead process or stale lease, restore baseline and verify driver readback.
4. Inspect %LOCALAPPDATA%/AmdTune/recovery/*.recovery.log for a clear RECOVERY VERIFIED record.
5. Check Adrenalin and fresh ADLX readback. If settings are still changed, manually Reset tuning to Default and STOP further testing.

## Gate 3 — failure scenarios

Use mocks and CI first: driver rejects writes, mismatch readback, stale/missing hotspot, corrupt compute output, cancellation, multiple concurrent sessions, readback timeout, dead guard. For physical tests, **never intentionally apply known crash-inducing clocks**. Confirm cancellation restores original settings.

## Submit evidence

Please share the sanitized output of:

- artifacts/hardware-doctor.log
- a single simulation audit JSON
- a single first-write hardware audit JSON
- any *.recovery.log (omit personal file paths if preferred)
- screenshots of before and after tuning values in Adrenalin

Do not share private identifiers, crash dumps or full system logs without reviewing them first.

## Decision criteria

The next stage (extended gaming verification, more workloads, power efficiency model, automatic candidate selection) can begin only after Gate 0/1/2 results are examined. **No percentage stability claim is ever warranted from short benchmark passes alone.**
