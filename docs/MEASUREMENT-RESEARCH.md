# AMD Tune measurement methodology and automated tuning safety gates

## Research background (November 15, 2026)

The first RX 9070 XT -15 mV experiment completed with all candidate checks correct and baseline restored, but its 3-repetition stock memory result drifted **-25.62%** between A/A reference blocks. With 7 repetitions, the drift improved to **+3.81%**, yet compute baseline CV was **29.23%**. The previous extended profile still measured individual GPU operations at 4–73 ms and sampled ADLX metrics during idle gaps; its apparently favorable scores cannot establish a tuning gain or efficiency improvement.

Sources and implications:

1. **Microsoft Direct3D 12 timing:** GPU queue timestamp queries, resolved through a query heap and divided by the command queue frequency, measure GPU-side work. The current ComputeSharp runner exposes *host dispatch + readback* latency, NOT GPU timestamp duration. GPU-only timing must be a future direct D3D12 integration, not silently estimated. https://learn.microsoft.com/en-us/windows/win32/direct3d12/timing
2. **Windows WDDM TDR:** a GPU command that cannot be completed/preempted promptly can trigger device recovery; the default TDR threshold is around two seconds. Retain bounded dispatch group counts (<=65,535 in one dimension) and short individual commands instead of packing a large iteration count into one shader invocation. Do **not** recommend editing TdrDelay. https://learn.microsoft.com/en-us/windows-hardware/drivers/display/timeout-detection-and-recovery
3. **AMD ADLX performance monitoring:** GPUUsage, GPUClockSpeed, GPUHotspotTemperature, and GPUTotalBoardPower are available as metrics. ADLX values are asynchronous samples, not clock-calibrated GPU timestamp events, and may be stale or represent idle during CPU validation. Do not infer gaming power savings from a single post-benchmark 12 W reading. https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/performance-monitoring/iadlxgpumetrics/
4. **NIST measurement-process characterization:** repeatability/precision and longer-term stability are separate characteristics. More repetitions do not correct systematic bias or environment drift. https://www.itl.nist.gov/div898/handbook/mpc/section1/mpc114.htm
5. **Google Benchmark methodology:** time-bounded warmup/measurement and repeated independent observations reduce the impact of short initialization overhead, but CPU scheduling/frequency/cache competition remain sources of variance. https://github.com/google/benchmark/blob/main/docs/user_guide.md
6. **PIX on Windows:** for independent GPU timestamp and hardware counter verification use PIX timing/GPU captures. Do not claim hardware GPU timestamps are integrated yet. https://learn.microsoft.com/en-us/windows/pix/gpu-captures/hardware-counters
7. **AMD's tuning warning:** any settings outside factory specification can create instability, corrupted images, data loss, or reduced device lifetime. Discovered ADLX driver ranges are **not** proof of safe overclock limits. https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/gpu-tuning/

## Four distinct questions

- **Correctness:** did each GPU result match an independent CPU oracle? Our sustained test uses sticky device-side error flags: a bad intermediate result detected by the next pass remains visible even when the next GPU pass overwrites the output. Final output AND all sticky flags are inspected on CPU. This detects some data corruption, not corrected memory errors or true ECC counts.
- **Reliability/stability:** do workloads complete repeatedly under changing temperatures, cooling states and real games without OS resets, driver removal/TDR, hangs or visible corruption? A few seconds of synthetic GPU work are only early evidence, not a universal stability rating.
- **Performance:** did a candidate produce repeatable gains greater than the same stock reference's noise, accounting for warmup and drift? Report actual per-kind CV, A/B/A drift and timing minimums, not only percent change. Host time is not pure GPU time.
- **Efficiency:** did performance-per-W improve under synchronized, sustained loaded telemetry? The current ADLX sampling is opportunistic and not GPU-timestamp calibrated. Do not calculate watt-hours, joules per frame or performance-per-W from incidental idle samples.

## Current implementation and boundaries

- A **sustained** opt-in profile (\`--sustained-benchmark\`) targets 750 ms of host-submitted GPU dispatch + readback per individual measurement. Each pass still uses the GPU dispatch planner's ≤1,048,576-element bounded slices. The 128 MiB memory data and separate 128 MiB sticky-error buffer fit in typical modern discrete VRAM. A hard cap of 16,384 dispatches per measurement prevents runaway work on very fast GPUs.
- Two untimed warmup passes precede the measured passes. Each shader pass validates the preceding GPU buffer and records any wrong intermediate value in a separate, sticky error buffer, then re-computes the deterministic expected value. The CPU checks every final output and every error flag; no intermediate pass is hidden by a later overwrite. A successful final-state check is **not** equivalent to a multi-hour game stress test.
- The independent recovery process remains armed for hardware write sessions. The engine samples ADLX hotspot, board power, core clocks and optional GPU usage concurrently, with thermal guard failure cancelling the workload and triggering rollback. Collected telemetry is explicitly marked asynchronous and may include CPU-oracle time; loaded readings are only those with reported GPU usage ≥50% and positive core clock.
- The audit stores raw snapshots, active sample counts and loaded-only summaries per measurement, plus a separate **TrialEvidence** model. The trial verdict distinguishes correctness, measurement quality and stability. Timing minimum ≥250 ms, per-phase repetitions ≥7, CV ≤5%, stock drift ≤3%, and loaded sampling coverage are provisional quality-control targets, **not** a guarantee of statistically significant performance gains.
- **Automatic tuning is not enabled.** Even if all in-session quality targets are met, its gate remains closed pending GPU-only timing validation, independent sessions, sustained stability and hardware crash-rollback fault injection. No hidden auto-apply or persistent profiles.

## Explicit next tests on the user's RX 9070 XT

1. From the feature branch, publish, run hardware doctor, and start **read-only** sustained verification first:

       git pull --ff-only
       ./scripts/publish-windows.ps1
       ./scripts/hardware-doctor.ps1
       ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark

   Expected \`GPU_VERIFY_PASS\`, no mismatches, identical original/final settings. A missing loaded sensor sample should be **reported as insufficient evidence**, never treated as a low power draw.

2. **Read-only repeated correctness soak** (1 minute; may take longer to finish a full workload cycle):

       ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark --soak-seconds 60

   Expected \`GPU_SOAK_PASS\`; audit written under \`%LOCALAPPDATA%\AmdTune\soaks\`. This continuously runs repeated bounded tests with CPU verification in between; it does **not** keep the GPU busy continuously for a full minute.

3. After these tests pass and independent verification shows stock unchanged, optionally run exactly **one** -15 mV candidate with unchanged VRAM/timings. Use the sustained profile and **3 repetitions for the first physical smoke**, then examine a report's rollback and telemetry:

       ./artifacts/app/AmdTune.Web.exe --hardware --enable-hardware-writes --sustained-benchmark

   Then \`./scripts/hardware-doctor.ps1\` and check AMD Adrenalin. If the first run passes, collect seven repetitions **only when the safety and thermal path is verified**, and compare separate sessions rather than inferring from one time-ordered A/B/A experiment.

4. With the test engine validated, independently reproduce loaded telemetry via PIX/Adrenalin and compare GPU-timestamp performance measurements. Only after physical crash-recovery validation, multiple independent sessions, sustained real workload tests and signed-off safety guards should an automatic one-candidate-at-a-time search be considered. It must stop on any mismatch, temperature excursion, driver reset, ambiguous timing, significant stock drift or unverified rollback. A memory-frequency sweep is a separate experiment; avoid changing voltage and VRAM at once.

## Planned improvements, explicitly not implemented

- Proper D3D12 command-queue timestamp queries and command-queue calibration.
- Dedicated high duty-cycle 1–5-minute stress phases with periodic independent correctness checks and loaded temperature/power sampling.
- Game-integrated frame-time distributions, 1% low FPS, sustained power efficiency, repeatability across cold/warm states.
- Experiment schedule randomization/interleaving and statistical modeling of independent sessions.
- Operator-confirmed hardware kill/recovery fault test.
- Auto-tuner bounded adaptive candidate selection, persisted experiment history, explicit abort and physical restart safety constraints.

Keep Production unchanged and the PR draft until these gates have physical evidence.

## November 15, 2026 — First sustained test, instrumentation issue

An operator-run read-only physical test started at driver settings **-50 mV voltage offset, 2700 MHz configured VRAM limit, Fast Timing**, NOT the earlier 0 mV / 2518 MHz / Default tuning baseline. These values were unchanged at the end of the test. This driver state must be recognized as a custom profile and must **not** be confused with a stock A/B/A baseline.

- Compute: 753.6 ms, 7,034 measured passes, all 1,048,576 outputs correct, no mismatches; 2 ADLX samples, 0 active samples.
- Memory: 808.8 ms, 228 measured passes over 32 bounded slices each, all 33,554,432 outputs correct, no mismatches; 2 ADLX samples, 0 active samples.
- Mixed: interrupted during telemetry sampler shutdown with `SafetyException: ADLX bridge timed out or was cancelled`. **GPU correctness unknown** for Mixed; do not label the setting unstable based on this error.
- Root cause in source: `WorkloadTelemetrySampler.StopAsync()` cancels its in-flight subprocess request and `BridgeProcess.InvokeAsync()` converted even caller-requested cancellation into `HardwareException`. The sampler interpreted that manufactured exception as a hardware timeout.
- Fix: preserve `OperationCanceledException` for caller-requested cancellation while **continuing to fail closed** for genuine internal 8-second ADLX timeouts, broken reads and thermal violations. Added deterministic mock-based concurrent-stop, recorder and failure-propagation regression tests.
- The local `publish-windows.ps1` intentionally preserves the existing native ADLX bridge if no C++ build tools are installed. The previous binary may lack the newly-added `utilizationPercent` JSON key, causing active telemetry to remain at 0 even when shaders run. `hardware-doctor.ps1` now checks this telemetry schema and warns if stale. Replace the native bridge from a trusted current GitHub Actions bundle (or rebuild with pinned AMD SDK) to test the new utilization metric.

**Next:** re-publish from a passing CI commit, refresh the native bridge if the schema is old, confirm the intentional custom profile versus factory-default baseline, and re-run `--hardware --verify-gpu --sustained-benchmark` without hardware writes. Stop on genuine sensor/thermal failures. No automatic tuning yet.


## Instrumentation iteration: streaming ADLX readings

The read-only 60-second soak at -50mV/2700MHz/Fast Timing (a custom profile) completed 60/60 correctness checks without errors, but obtained only 81 ADLX snapshots, none of the 60 measurement windows satisfying >=3 active samples. Root cause: the old native bridge initialized ADLX on every individual reading. This iteration introduces a bounded 100–1000ms `telemetry-stream` mode (125ms requested by managed workload sampler) that emits NDJSON from one helper process **per workload** rather than one new process per reading. The separate native `telemetry-stream-capabilities` command works even without an AMD GPU so CI can verify protocol compatibility. New monitored workload behavior must pass read-only physical verification before further write experiments. Sampler failures and hotspot violations still cancel tests; no auto-overclock decision is enabled.

## November 15: stream instrumentation correlation and interference follow-up

The user's physical RX 9070 XT read-only stream diagnostic at **-50mV / 2700MHz / Fast timings** completed all three 750-837ms correctness checks without a mismatch, and original driver settings were identical before and after. Native ADLX sampling increased from 1–2 observations per workload to **7 Compute, 8 Memory and 5 Mixed**, yet `ActiveSamples=0` for all three. The application's **provisional** active definition requires reported `GPUUsage >= 50%` and a positive clock. This is a data-classification/measurement-quality failure, **not** a GPU correctness or driver stability failure.

Host-measured synthetic throughput also decreased compared with the immediately preceding physical single-run values at the same *configured* profile: Compute 9.566 → 7.993 billion processed elements/s (~-16.4%), Memory 10.814 → 8.136 (~-24.8%) and Mixed 10.459 → 10.042 (~-4.0%). **Causation unproven**: extra native metric requests may contend with GPU/CPU execution, but system activity, GPU clock/power states and run-to-run variability are possible confounders. Do not claim accurate GPU-only throughput or infer improved efficiency from sparse readings.

AMD's official `IADLXGPUMetrics::TimeStamp` returns metric *acquisition* time in milliseconds since the epoch. Separate ADLX `GetCurrentGPUMetrics` calls need not produce new samples at the requested native helper polling cadence. AMD specifically recommends `GetGPUMetricsHistory` for continuous collection. Therefore, this iteration records `metricTimestampMs` directly from ADLX alongside the host observation time, reports *distinct* source timestamps and repeated source reads, and calculates minimum/maximum/mean reported utilization even when none meets the 50% heuristic. Duplicate source timestamps are **not** counted as independent loaded samples. If source timestamps are unavailable, evidence remains provisional. Source: https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/performance-monitoring/iadlxgpumetrics/timestamp/ and https://gpuopen.com/manuals/adlx/adlx-sdk-references/adlx-interfaces/performance-monitoring/iadlxperformancemonitoringservices/getcurrentgpumetrics/

A read-only-only `--telemetry-interval-ms` parameter accepts 100–1000 ms when and only when `--hardware --verify-gpu` is active, never for a tuning-write session. This provides a controlled way to compare monitoring interference at 125 ms versus 500 ms without disabling thermal safety or applying settings. The interval and raw readings are included in the soak audit.

**Important experiment design:** keep the currently configured Adrenalin profile *identical across both interval-comparison runs*. Separately, save that custom profile, reset to Adrenalin Default and verify settings before collecting factory-stock performance baselines. The interval comparison is a diagnostic, not a default-vs-tuned performance proof. Avoid performance-based auto-tuning decisions until the collector has independently verified cadence and queue-calibrated GPU timing.

## November 15 — stock 125ms versus 500ms read-only telemetry diagnostic

The operator restored AMD Adrenalin factory-default tuning and verified driver
readback `0 mV / 2518 MHz configured VRAM limit / Default timings` before and
after both **read-only** GPU validation commands. All six actual RX 9070 XT
workloads passed with 0 mismatches and no settings changes.

| Workload | 125ms host-timed synthetic elements/s | 500ms host-timed synthetic elements/s | 500ms minus 125ms |
|---|---:|---:|---:|
| Compute | 9.169 billion/s | 9.263 billion/s | +1.02% |
| Memory | 9.617 billion/s | 9.335 billion/s | -2.93% |
| Mixed | 9.126 billion/s | 10.813 billion/s | +18.49% |

At 125ms requested sampling, the native source metric intervals averaged
140/134/140ms, with **18/18 distinct** samples in total and **15** qualifying
as active. At 500ms requested sampling, 6/6 distinct readings and six active
samples were observed; native metric intervals averaged 502/513/504ms.
The previous `ActiveSamples=0` observation did not recur and was not a GPU
correctness problem. This confirms sensor throughput and source timestamp
deduplication, but does **not** demonstrate 500ms benchmarking faster:
each configuration has **only one measurement per workload**, nonrandomized
in temporal order. The 18.49% Mixed difference is a strong reason to avoid
attributing the difference to polling overhead.

The native `metricTimestampMs` value near **213 million** does not match
a contemporary Unix-epoch millisecond time. Official ADLX documentation calls
the field an epoch time, but this observed numeric value is incompatible with
the corresponding UTC timestamp in the audit. Treat this ADLX-provided value
as a **source sample identity and relative interval** until its absolute
clock domain is verified. Do not align it to system UTC or GPU command timestamps
based on the documentation alone.

New opt-in **read-only crossover** command:
`--hardware --verify-gpu --sustained-benchmark --compare-telemetry`
runs two ABBA/BAAB rounds at 125 and 500ms within one process, per workload
(four measurements per interval and kind), with the **same seed within each
round**, active thermal monitoring and driver readback verification. It stores
all raw sensor measurements and descriptive score statistics in
`%LOCALAPPDATA%/AmdTune/soaks/*.comparison.json`.
It **cannot** apply any voltage/memory clock changes and does not claim a
statistically significant performance difference. It is a first controlled
repeatability diagnostic, not GPU queue timestamp measurement or efficiency
validation. Automatic tuning is still disabled.

## Physical ABBA crossover and necessary host-window scoping

The stock 24-measurement `--compare-telemetry` crossover passed correctness and verified driver settings were unchanged. Its Compute host throughput appeared 5.23% higher with *more frequent* 125ms readings, while Memory and Mixed varied only +0.22% and +0.38% (500ms minus 125ms). This mixed result makes a universal sampling-overhead performance conclusion unwarranted. See [hardware validation](HARDWARE-VALIDATION.md).

**Root-cause candidate for implausibly low ADLX utilization:** The old `WorkloadTelemetryRecorder` captured from its invocation start through completion of CPU oracle validation. That envelope included CPU reference initialization, native ADLX device discovery, GPU buffer allocation, warmup and post-readback CPU checks. It was not restricted to the 750ms host-timed GPU dispatch/readback phase. The new `HostTimedWindow` is generated by the runner immediately before timing begins and immediately after it ends. The sampler still validates hotspot on all native readings (including before/after the window), but restricts per-workload *efficiency evidence* to sensor observations acquired during the host-timed interval. The new audit explicitly records in-window/total samples and source-timestamp-distinct counts. Because ADLX snapshots may themselves reflect a preceding averaging period, even this is not calibrated GPU-to-sensor synchronization.

Microsoft's D3D12 timing specification requires query heap timestamp values from a command list and the originating command queue's `GetTimestampFrequency()` to convert to elapsed GPU time. Future integration must ensure timestamps bound **the same actual ComputeSharp GPU command queue**, and correctly resolve queries after GPU completion; measuring an unrelated second queue is invalid. See https://learn.microsoft.com/en-us/windows/win32/direct3d12/timing and https://learn.microsoft.com/en-us/windows/win32/direct3d12/queries. This is a future architecture milestone, not yet an implemented capability.

## Five-minute read-only block study (next research gate)

The operator requested more than isolated 10–30-second runs so that
measured variation, warmup drift and telemetry overhead can be assessed
with materially more evidence. A single `--study-seconds 300` session
performs complete nine-measurement blocks, each testing all three GPU
workloads at **125ms / 250ms / 500ms** ADLX stream intervals.

The benchmark consists of repeated ~750ms bounded GPU dispatch/readback
windows, with independent CPU final-state and sticky-error verification
between windows. The first complete block contributes to correctness
testing but is excluded from scored descriptive statistics. Subsequent
blocks rotate both workload and sensor polling order, reducing the most
obvious time-order confounding from first-vs-last comparisons. The study
records throughput mean/median/CV, first-half vs second-half drift,
within-block paired interval score ratios, actual host-timed GPU seconds,
the number of individual workloads, peak sampled hotspot and
in-window/active/total ADLX samples. Raw observations and sensor traces
are retained for offline visualization.

Hard constraints: read-only flags only, exact initial settings re-checked
after every complete block and at session end; per-measurement 90-second
timeout and 90 C hotspot guard; hardware correctness mismatches and failed
ADLX telemetry stop the study. Partial reports are stored even when a
failure occurs. No adaptive clocking, voltage writes, hidden changes or
stable-overclock claims. The operator should keep the RX 9070 XT at
known stock defaults and stop unrelated GPU-intensive applications.

The study is a **performance measurement / correctness soak**, not a
continuous GPU-thermal stress workload, despite its five-minute session
wall time. Long-term stability and GPU-only queue timestamp timing remain
unvalidated.

## High-load graphics and thermal monitoring versus synthetic correctness

After a five-minute stock RX 9070 XT study at 0mV / 2518MHz / Default
timings, all 297 synthetic GPU correctness tests passed, but maximum
sampled hotspot was ~29°C and board power was far below its rated
graphics workload level. Core clocks fluctuated and many source
metrics appeared unchanged within a short workload despite distinct
ADLX timestamps. The study establishes repeatability characteristics
of the specific bounded synthetic shader, **not** sustained graphics
or VRAM error stability.

Research showed no generally accepted drop-in NuGet package that
validates full-load Direct3D graphics and VRAM errors end-to-end.
ComputeSharp (already in the repo) is a DirectX 12 compute programming
library, **not** a finished GPU gaming stability test suite. FurMark 2
provides Vulkan/OpenGL stress modes and time-bounded CLI arguments
(`--demo furmark-vk --max-time 60`). OCCT provides graphics and VRAM
error tests but its CLI is an Enterprise-only offering. 3DMark CLI
requires Enterprise/Professional licensing.

To gather independent GPU power/clock/thermal evidence while an external
validated program supplies load, AMD Tune now supports a **strictly
read-only passive monitoring CLI**:
`--hardware --monitor-external-seconds 60 --external-load-label FurMark2-Vulkan`.
The native ADLX stream gathers raw timestamped samples to a bounded
16,384-reading buffer; analysis saves per-minute sensor power,
utilization, clocks, hotspot and high-load proportion, distinguishing
unique source timestamps and unique metric-value states. The
temperature guard remains at 90°C, plus fail-closed sensor errors
and driver settings readback.

A critical limitation is that AMD Tune does **not** launch or
terminate the external workload, so a sensor/thermal abort cannot
automatically shut the test down. The operator must do so manually.
A monitor pass means only sensor capture succeeded; load success,
rendering correctness, VRAM error detection and crash information
come from the independent stress application. No external tool's
command line or output can be treated as evidence of GPU correctness
unless the tool's reports are collected and verified.

FurMark 2 CLI guide: https://geeks3dsoftware.com/furmark/command-line/
FurMark vendor: https://geeks3d.com/furmark/
OCCT Enterprise CLI: https://ocbase.com/occt/enterprise
3DMark CLI licensing: https://support.benchmarks.ul.com/support/solutions/articles/44002145411-windows-run-3dmark-benchmarks-from-the-command-line
ComputeSharp NuGet: https://www.nuget.org/packages/ComputeSharp/

## Supervised external FurMark 2, runtime containment and games

A vendor-supported FurMark 2 CLI permits deterministic Vulkan stress
execution via `--demo furmark-vk --width 2560 --height 1440 --max-time
<seconds>`, and `--print-render-speed` emits rendering diagnostic
information where supported. The new supervised runner validates an
absolute `furmark.exe` path and an explicit bounded duration
(30-600s), hashes the local executable (provenance record, *not* an
authenticity guarantee), launches a single process without a shell,
captures only bounded stdout/stderr tails and owns a second independent
`duration+30s` process-lifetime deadline.

The native ADLX telemetry stream remains fail-closed under a 90°C
hotspot guard. Sensor failures request cancellation and the owned
process-tree kill; the same cleanup occurs on console Ctrl+C or a
supervisor timeout. Initial/final tuning settings are compared and
the audit includes process exit code, abnormal early termination,
observed GPU loading and minutely power/hotspot/clock data. **Hardware
validation is still required**. A FurMark process exit status cannot
certify computational correctness or VRAM error freedom, and exact
FurMark performance output may not be available via redirected stdout.
An independent FurMark/OCCT/driver error signal or screenshot cannot
be invented from empty output.

Gaming performance should be captured separately using an ETW
present trace such as Intel PresentMon, ideally from fixed scripted
in-game benchmarks or repeatable scene captures, with controls for
resolution/quality, vsync, frame-generation/upscaling, driver state,
background load, and repeated cold/warm sessions. PresentMon's
`--process_name <exe> --output_file <csv> --timed <seconds>` captures
per-present timing data, from which distributions, average FPS and
low-percentile performance can be derived. AMD Tune does **not**
currently parse or command PresentMon; it must not infer 1% lows or
rendered FPS from ADLX utilization.

The automatic search remains gated: the external supervisor does not
apply GPU settings. Crash-termination rollback on the physical device
must be tested before allowing even one supervised write-enabled
candidate, then stability and performance validation must be separate
from candidate selection. Stop at the first mismatch/reset/hotspot
guard failure, and restore original settings.

FurMark CLI reference: https://furmarksoftware.org/furmark/command-line/
PresentMon CLI reference: https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md
