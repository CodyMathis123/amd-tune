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
