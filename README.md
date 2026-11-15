# AMD Tune

Evidence-led AMD GPU tuning experiments. **Safe by default: starts in SIMULATION mode and never changes hardware.**

This is an experimental, transparent C#/.NET 10 system for discovery, real GPU correctness workloads, nonpersistent voltage/VRAM trials, driver readback, rollback, audit reporting and a local browser UI. It is not a proven production overclocking utility. No code claims ECC correction counters or a scientifically justified percentage of gaming stability.

## What is implemented

| Component | Status |
| --- | --- |
| Loopback-only browser dashboard and JSON API | Implemented |
| Read-only GPU capabilities / tuning settings / telemetry discovery | AMD ADLX bridge; requires Windows, AMD drivers, locally built bridge |
| Explicit nonpersistent voltage/VRAM experiment | Implemented with conservative policy; **hardware validation pending** |
| Capability-aware guided voltage sweep | Implemented; generates conservative, driver-range-valid candidates and restores after each trial |
| DirectX 12 integer correctness workload | ComputeSharp 3.2.0; checks every output element against CPU oracle; hardware validation pending |
| Simulation and dependency-injected mocks | Implemented |
| Independent process recovery after host crash | Startup/disarm verified on a physical Windows machine; restoration after a hardware trial remains unverified |
| Rollback after success, failure, cancellation | Implemented; driver readback verified |
| JSON audit records plus crash-survivable JSONL event journal | Implemented at LocalAppData/AmdTune/reports |
| Long-running gaming stability prediction | **Not claimed**; out of scope until validated |
| Actual ECC error detection | **Not supported** without trustworthy exposed counters |
| Auto-apply at Windows startup | **Deliberately absent** |
| Linux tuning | **Not implemented**; no speculative sysfs writes |

> WARNING: This is pre-release engineering software. Do not enable physical writes until the hardware-smoke checklist is complete and native bridge / recovery process are tested.

### Prerequisites

- Windows 11, AMD Radeon driver supporting ADLX 2.0, Microsoft .NET 10 SDK.
- To build the AMD native adapter: Visual Studio (2022 or newer) or Build Tools with **Desktop development with C++**, the MSVC x64 toolset, a Windows SDK, and the official [AMD ADLX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX). The AMD SDK has its own license; it is not vendored here.
- For web-only simulator, Windows or a .NET 10 platform; no AMD driver or GPU needed.
- Do **not** run as administrator by default. AMD driver access may require OS permissions; check read-only discovery first.

### 1. Run simulation (zero hardware writes)

From the repo root:

    dotnet restore AmdTune.slnx
    dotnet test tests/AmdTune.Tests/AmdTune.Tests.csproj -c Release
    dotnet run --project src/AmdTune.Web/AmdTune.Web.csproj

Open http://127.0.0.1:5783. The title and results clearly state SIMULATION. Use **Preview conservative voltage sweep** for a three-candidate guided plan, or enter one candidate manually. This does not activate hardware in simulation mode.

### 2. No Visual Studio required: download complete Windows development build

A standalone **unsigned development bundle** is generated on every passing PR build. The package includes the published .NET app, separate recovery process, and C++ ADLX bridge compiled by GitHub Actions against the pinned official AMD SDK. You can keep using VS Code without installing Visual Studio or its Build Tools.

1. Open the successful GitHub Actions workflow for this PR, then download the artifact named **amd-tune-windows-x64-unsigned-dev**. Confirm the workflow comes from your repository.
2. Extract the ZIP to a folder of your choice. Keep all files together, including the bridge and recovery executable. DEVELOPMENT-BUILD.txt records the build commit and SHA256SUMS.txt records executable hashes.
3. Run AmdTune.Web.exe without arguments for **simulation mode**.
4. For **read-only hardware discovery**, run AmdTune.Web.exe --hardware from the extracted folder. Alternatively, extract into the repository's artifacts/app folder and run scripts/hardware-doctor.ps1 from the repository root.

Do not enable hardware writes until the full hardware validation checklist is satisfied. This SDK bridge has compiled in CI but has not been runtime-tested on your RX 9070 XT.

#### Alternative: compile the native adapter yourself

You do **not** need the full Visual Studio IDE. Install standalone Microsoft Visual Studio Build Tools, select **Desktop development with C++** with MSVC x64 and Windows SDK, and rerun build-native.ps1. VS Code alone does not install cl.exe or the Windows C++ toolchain.

### 2. Build official native ADLX bridge

Clone or download the AMD SDK under your own SDK license. You can run from **ordinary PowerShell** or Visual Studio Developer PowerShell. The native build script uses Microsoft's vswhere and Developer PowerShell launcher to initialize MSVC x64 if installed:

    git clone https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git third_party/ADLX
    git -C third_party/ADLX checkout --detach 32b5a740d42295c5dfe9026b9f52683da0f3af91
    ./scripts/build-native.ps1 -AdlxSdkDirectory ./third_party/ADLX
    ./scripts/publish-windows.ps1
    ./scripts/hardware-doctor.ps1

If you already have third_party/ADLX, skip the clone and keep the pinned revision. The compiler must come from the Visual Studio C++ workload; installing .NET alone is not enough. These commands build the bridge from a pinned AMD ADLX revision (32b5a740d422), publish the UI and separate recovery executable in the same folder, and run **read-only** hardware diagnostics. Do not trust a random downloaded bridge .exe.

#### Troubleshooting native compilation

- `cl.exe not found` means the MSVC x64 build environment is not available. The build script now finds installed MSVC via vswhere and initializes the developer shell automatically. If it still cannot find the compiler, open Visual Studio Installer, choose Modify, and install **Desktop development with C++** (MSVC x64 and Windows SDK).
- If your shell blocks trusted local scripts, prefer a temporary process-scoped execution-policy change rather than modifying the machine-wide policy.
- A `git clone` error indicating the ADLX directory already exists is harmless if you have checked out the pinned revision; skip the clone.
- Running publish-windows.ps1 before the native bridge exists builds only the simulation-capable app. Once build-native.ps1 succeeds, **rerun publish-windows.ps1** so the bridge is copied into artifacts/app, then rerun hardware-doctor.ps1.

### 3. Hardware discovery, without enabling writes

    ./artifacts/app/AmdTune.Web.exe --hardware

The UI shows actual ADLX capabilities, raw driver limits and device name; candidates cannot be submitted.

### Paired stock telemetry comparison — ABBA/BAAB read-only

To avoid reading too much into a single 125ms versus 500ms comparison,
AMD Tune has a gated read-only monitoring-crossover diagnostic:

    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark --compare-telemetry

The command uses a single fixed driver-tuning profile, runs two
125/500/500/125 and 500/125/125/500 rounds for **each** GPU workload,
checks all results against the CPU reference, and records both
per-workload descriptive score distributions and raw telemetry evidence.
No hardware tuning writes or stability verdicts are permitted.
Results are stored under `%LOCALAPPDATA%/AmdTune/soaks` as
`*.comparison.json`; sample size is only four per condition, so
do not interpret the reported difference as a causal effect or a
statistically reliable performance gain. It does not use GPU queue timestamps.

### Diagnosing ADLX stream publication cadence and monitoring overhead

If the sustained read-only test reports `ActiveSamples=0` despite
correct GPU execution, **do not** assume the GPU was idle. The current
heuristic requires reported utilization >=50%; the next diagnostic prints
the actual utilization range and the number of distinct AMD metrics
timestamps (the native helper may read the same published sample
multiple times). This diagnostic is read-only and keeps thermal guards:

    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark --telemetry-interval-ms 125
    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark --telemetry-interval-ms 500

The new `metricTimestampMs` comes from AMD ADLX and can distinguish
a new source measurement from a repeat observation of the same sample.
The two tests must run at the same Adrenalin tuning settings for a
meaningful comparison of measurement *interference*. The sampling
interval override is disallowed with `--enable-hardware-writes`.
Neither result establishes stock overclocking performance or sustained
efficiency. Save your custom Adrenalin profile and restore defaults
before collecting factory-stock reference sessions.

## Native read-only telemetry streaming (workload instrumentation iteration)

The 60-second soak recorded only **81 ADLX samples over 60 measurements**.
The original monitoring implementation started a separate native C++ process
for *each* sample. A fresh read-only `telemetry-stream <interval-ms>` command
now keeps AMD ADLX initialized inside **one helper process per workload** and
emits timestamped JSON lines (NDJSON), normally sampled at 125 ms intervals.
The helper is explicitly read-only; GPU settings writes use the existing
separate `apply` command. The previous per-reading path remains available
to `hardware-doctor.ps1` and simulation tests.

The managed sampler automatically chooses the native stream when available;
it validates every reading against the live hotspot guard, captures bounded
raw telemetry and treats a broken or silent stream as a safety failure.
Stopping a workload cancels the helper, without producing a spurious failure.
The native process has an 8-second no-data timeout and must terminate when
the managed sampler is disposed. Hardware stability, sampling cadence and
accuracy remain **unverified on the physical GPU** until the next read-only
test; sampling may still include periods before and after GPU execution.

To avoid `gh run download` extraction conflicts when the target file exists,
there is a dedicated helper that fetches the verified CI artifact into a
temporary empty folder, checks its protocol, and copies only the trusted
executable into `artifacts/native`:

    git pull --ff-only
    ./scripts/update-native-bridge.ps1 -RunId <successful-CI-run-ID>
    ./scripts/publish-windows.ps1
    ./scripts/hardware-doctor.ps1
    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark

The doctor now reports `Native read-only ADLX telemetry streaming` separately
from the existing `utilizationPercent` field. If stream support is missing,
update the native helper before launching the new read-only validation. Stop
on a sensor failure or incorrect GPU output. No hardware tuning writes,
background auto-tuning or production release are enabled.

### Sustained host-timed profiling and separate stability evidence

The new opt-in `--sustained-benchmark` profile targets at least 750 ms
per host-timed DX12 workload (unless the hard dispatch cap is reached). It
warms the GPU and repeats bounded passes while using sticky error flags to
preserve evidence of intermediate mismatches. Unlike the quick and extended
profiles, this mode is intended to improve the *duration* of each host-side
measurement. It does **not** implement native GPU timestamp queries.

Native ADLX now optionally reports utilization percentage, and physical
tuning trials save concurrent telemetry snapshots with a per-workload
sampling-quality record. The explicit trial evidence gate refuses to enable
automatic tuning without reliable host windows, stable stock references,
active GPU telemetry, independent long-run stability and crash recovery.

Start with read-only tests:

    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark
    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --sustained-benchmark --soak-seconds 60

The second command exercises 60 seconds of *repeated correctness checks*,
not 60 seconds of uninterrupted graphics load; individual checks include
CPU oracle time. Both commands forbid hardware tuning writes.
Diagnostic JSON reports are stored under `%LOCALAPPDATA%/AmdTune/soaks`.
Only after read-only validation should you consider a **single**, supervised
-15mV nonpersistent tuning experiment using
`--hardware --enable-hardware-writes --sustained-benchmark`.
Never mix scores from different profile types.

See [measurement methodology](docs/MEASUREMENT-RESEARCH.md) for sources,
criteria, limitations, and the gated roadmap toward automatic tuning.

### Extended benchmarking profile (opt-in; recommended next validation)

Existing short run measurements are just ~5–70 ms per workload and do not establish
steady-state GPU clocks, performance gains or long-term stability. The optional
`--extended-benchmark` flag enables a more demanding but **bounded** profile:
2 GPU warmup passes per workload (untimed) and 16 measured passes for Compute,
12 for Memory, and 16 for Mixed. Each pass is split into the same small,
DX12-compliant dispatches; no single giant shader dispatch is introduced.
Correctness still checks **every final output** using a CPU reference which
accounts for warmup and measured passes. Intermediate GPU states are not
individually verified; no ECC error counters are claimed. This can take
substantially longer, especially due to CPU oracle checking.

**Start with an extended read-only hardware check first:**

    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu --extended-benchmark
    ./scripts/hardware-doctor.ps1

Only after it passes, consider another **single** -15 mV hardware experiment
with 3 UI repetitions, not a sweep. Run the server with:

    ./artifacts/app/AmdTune.Web.exe --hardware --enable-hardware-writes --extended-benchmark

The extended profile improves warmup and per-measurement GPU work, but it is not
a multi-minute stress test. Measured times are CPU-submitted dispatch + transfer
times, not isolated GPU timestamp-query results; no sustained GPU telemetry is
currently recorded. Compare reports **within the same benchmark profile**, not
against quick-profile scores, which use different timing boundaries.
Stop on correctness errors, driver resets, unverified rollback, or abnormal
temperatures; verify original settings in Adrenalin after any write test.

### 3B. Read-only GPU correctness diagnostic

Before any tuning write, validate the actual DirectX 12 compute workloads at
current GPU settings:

    ./artifacts/app/AmdTune.Web.exe --hardware --verify-gpu
    ./scripts/hardware-doctor.ps1

This executes real GPU Compute, Memory and Mixed workloads and checks every
returned element against a deterministic CPU reference. It is **read-only**:
no tuning operation, candidate, watchdog lease, or settings application occurs.
A large memory buffer is divided into bounded 1D DX12 dispatches to respect
the 65,535-groups-per-dimension limit; the original 128 MiB single-dispatch
attempt failed with `groupsX=524288` on an RX 9070 XT.

The diagnostic prints `GPU_VERIFY_PASS` only when all three workloads agree
with the CPU oracle and the original driver tuning settings are unchanged.
Any errors block the hardware-write smoke test.

### 4. Hardware-write smoke test (only once read-only discovery is verified)

Read [docs/HARDWARE-VALIDATION.md](docs/HARDWARE-VALIDATION.md), stop other GPU-tuning tools, save your Adrenalin profile, and ensure recovery logs can be collected. After you understand the rollback limitations, opt in explicitly:

    ./artifacts/app/AmdTune.Web.exe --hardware --enable-hardware-writes

The initial physical trial should be **one small -15 mV voltage offset, no VRAM overclock, three repetitions**. Only try the guided multi-candidate sweep after the single-candidate write and recovery gates pass. Observe readback and rollback. Do not enable persistent tuning.

### Architecture

- **Core**: immutable models, hardware contracts, policy, nonpersistent experiment state machine, measurement statistics.
- **Infrastructure**: deterministic fake GPU, JSON audit, strictly typed ADLX subprocess adapter, independent recovery lease.
- **GpuWorkloads**: DX12 compute shader with deterministic integer oracle; rejects WARP, checks target GPU model.
- **Recovery**: independent watchdog executable; restores baseline if host disappears or lease heartbeat stops.
- **Web**: browser UI served only from 127.0.0.1:5783, host/origin/CSRF checks, async run status and cancellation.
- **Native**: narrow C++ ADLX adapter built with the official SDK, never shipped as an opaque executable.

The projects separate concerns via interfaces. Changing the UI must not change policy or hardware code.

### Known limitations

- Real bridge compilation and actual GPU behavior are pending tests on a Windows RX 9070 XT. Simulator tests do not prove device safety.
- ComputeSharp selects its default DirectX adapter; we refuse runs if its recognizable RX model differs from ADLX. Identical GPUs need stronger identity matching in a future extension.
- Reported throughput includes transfers and is **not** a calibrated hardware bandwidth or GFLOPS measurement.
- A GPU crash can still freeze Windows. The recovery process cannot operate across power loss or an OS-level hang; the app never creates a logon auto-apply task. The watchdog pulses independently from the benchmark every two seconds and stops after a 120-second experiment forward-progress stall. Each GPU workload has a 90-second cancellation budget. Some driver hangs cannot be interrupted by managed cancellation.
- The workload mix and duration are insufficient to validate long-term gaming stability. Use independent stress and real games for confirmation.
- This version never retains tuned settings after a completed experiment; candidate export/persistent application require separately designed validation.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/HARDWARE-VALIDATION.md](docs/HARDWARE-VALIDATION.md).

## References / engineering decisions

- [AMD ADLX SDK 2.0](https://gpuopen.com/adlx/) — Windows monitoring/manual tuning APIs and their supported ranges.
- [Official ADLX C# examples](https://gpuopen.com/manuals/adlx/programming-with-adlx/adlx-samples/csharp-samples/) — C# bindings exist but depend on a native SDK/SWIG toolchain. We use a small auditable C++ CLI to isolate native lifecycle issues.
- [ComputeSharp 3.2.0](https://www.nuget.org/packages/ComputeSharp/3.2.0) — available .NET 8+ DirectX 12 compute library; no arbitrary hand-written GPU-benchmark numbers.
- [.NET 10 support](https://dotnet.microsoft.com/en-us/platform/support/policy) — LTS through November 2028.

## Contributing

MIT license for our code. AMD ADLX SDK is **not** MIT; do not copy its sources into this repository. Run tests and review safety policy changes carefully. No hardware writes in CI.
