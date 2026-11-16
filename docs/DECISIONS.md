# Evidence-based architectural decisions

## ADR-001 — .NET 10 with modular local UI
Decision: separate the immutable domain/policy code, hardware adapters, DX12 workloads, watchdog, and loopback UI. Simulation is cross-platform and read/write dependencies are replaceable.

Evidence: [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

## ADR-002 — Official AMD ADLX v2 adapter, pinned SDK
Decision: locally compile a small C++ helper against [the official ADLX SDK at commit 32b5a740d42295c5dfe9026b9f52683da0f3af91](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/tree/32b5a740d42295c5dfe9026b9f52683da0f3af91). No vendor binaries are copied into the repository.

Evidence: official SDK headers define GetGPUVoltageRange, GetGPUVoltage, SetGPUVoltage, GetMaxVRAMFrequencyRange, GetMaxVRAMFrequency, SetMaxVRAMFrequency, memory timings and GPU monitoring. CI verifies that the bridge compiles against this pinned SDK.

Limitation: compilation does not prove runtime driver support on an RX 9070 XT; hardware doctor must validate real ranges, readback, and telemetry.

## ADR-003 — Deterministic GPU correctness
Decision: use [ComputeSharp 3.2.0](https://www.nuget.org/packages/ComputeSharp/3.2.0), integer compute shaders and independent CPU calculations for all returned elements.

Limitation: end-to-end verified elements/s includes data transfer and CPU overhead. It is not GDDR6 GB/s, GFLOPS, or a true ECC correction count.

## ADR-004 — Nonpersistent tuning, conservative recovery
Decision: candidates require a separate recovery process, thermal prechecks and driver readback. The engine restores original values after every candidate and on cancellation/error. No persistent tune or logon task is created.

Limitation: no process can recover GPU settings during a complete OS freeze or power loss.

## ADR-005 — Repeatability and no exaggerated claims
Decision: use baseline/candidate/baseline (A/B/A), repeated measurements, drift/variance checks, immutable reports, and a write-ahead event journal. Simulation results cannot be accepted as hardware proof.

Limitation: thresholds are engineering guards, not a statistically measured probability of gaming stability.

## Gate for Production
A real RX 9070 XT must pass read-only discovery, a conservative single-candidate experiment, verified rollback, GPU correctness checks and watchdog fault-injection. Until then, keep the pull request in draft and do not claim a hardware-validated release.
