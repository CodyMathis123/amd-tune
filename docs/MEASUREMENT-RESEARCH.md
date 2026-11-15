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
