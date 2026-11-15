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
| Independent process recovery after host crash | Implemented; **must fault-inject and validate on Windows hardware** |
| Rollback after success, failure, cancellation | Implemented; driver readback verified |
| JSON audit records plus crash-survivable JSONL event journal | Implemented at LocalAppData/AmdTune/reports |
| Long-running gaming stability prediction | **Not claimed**; out of scope until validated |
| Actual ECC error detection | **Not supported** without trustworthy exposed counters |
| Auto-apply at Windows startup | **Deliberately absent** |
| Linux tuning | **Not implemented**; no speculative sysfs writes |

> WARNING: This is pre-release engineering software. Do not enable physical writes until the hardware-smoke checklist is complete and native bridge / recovery process are tested.

### Prerequisites

- Windows 11, AMD Radeon driver supporting ADLX 2.0, Microsoft .NET 10 SDK.
- To build AMD native adapter: Visual Studio 2026 C++ Desktop Build Tools and official [AMD ADLX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX). The AMD SDK has its own license; it is not vendored here.
- For web-only simulator, Windows or a .NET 10 platform; no AMD driver or GPU needed.
- Do **not** run as administrator by default. AMD driver access may require OS permissions; check read-only discovery first.

### 1. Run simulation (zero hardware writes)

From the repo root:

    dotnet restore AmdTune.slnx
    dotnet test tests/AmdTune.Tests/AmdTune.Tests.csproj -c Release
    dotnet run --project src/AmdTune.Web/AmdTune.Web.csproj

Open http://127.0.0.1:5783. The title and results clearly state SIMULATION. Use **Preview conservative voltage sweep** for a three-candidate guided plan, or enter one candidate manually. This does not activate hardware in simulation mode.

### 2. Build official native ADLX bridge

Clone or download the AMD SDK under your own SDK license, then run a Visual Studio Developer PowerShell:

    git clone https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git third_party/ADLX
    git -C third_party/ADLX checkout --detach 32b5a740d42295c5dfe9026b9f52683da0f3af91
    ./scripts/build-native.ps1 -AdlxSdkDirectory ./third_party/ADLX
    ./scripts/publish-windows.ps1
    ./scripts/hardware-doctor.ps1

These commands build the bridge from a pinned AMD ADLX revision (32b5a740d422), publish the UI and separate recovery executable in the same folder, and run **read-only** hardware diagnostics. Do not trust a random downloaded bridge .exe.

### 3. Hardware discovery, without enabling writes

    ./artifacts/app/AmdTune.Web.exe --hardware

The UI shows actual ADLX capabilities, raw driver limits and device name; candidates cannot be submitted.

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
- A GPU crash can still freeze Windows. The recovery process cannot operate across power loss or an OS-level hang; the app never creates a logon auto-apply task.
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
