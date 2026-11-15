# Architecture and threat model

## Invariants

1. Simulation is the default; only explicit --hardware + --enable-hardware-writes can make physical writes.
2. No driver write occurs until capabilities, original settings, thermal telemetry and independent guard have been verified.
3. All candidate inputs obey both the live driver ranges and a stricter hardcoded safety envelope.
4. A candidate is read back after write. Failure aborts the session.
5. Every candidate is restored before the next. Original settings are restored in finally and read back, with a timeout independent of user cancellation.
6. The recovery process is armed *before* physical writes. A missing or unresponsive guard blocks writes.
7. There is no persistent tuning profile, auto-apply scheduled task, elevation request, network telemetry or third-party executable bundle.
8. GPU correctness is an independently computed integer oracle, not a magic performance threshold.

## Process boundaries

Browser (same-origin localhost) -> ASP.NET Core Web -> Core TuningEngine -> IGpuDevice -> typed C++ ADLX CLI
                                                        -> IWorkloadRunner -> ComputeSharp DX12
                                                        -> IRollbackGuard -> independent Recovery process
                                                        -> IAuditStore -> write-once JSON

The browser can request only declared model-based operations. Arbitrary file paths, shell commands and arbitrary ADLX calls are not accepted by the API.

## Recovery limitations

The watchdog checks parent-process liveness and the lease file's recent modification time. If either fails, it applies the **baseline snapshot**, verifies ADLX readback, and leaves a log. The watchdog and browser must be launched under a user account allowed to use ADLX. On complete OS hangs it cannot respond. Do not interpret this as universal crash protection.

## Threat model

- DNS rebinding / remote control: bind only 127.0.0.1, check Host and Origin, require session-random CSRF token on POST.
- Unexpected termination: standalone guard independent of web process; never auto-start in a modified tuning state.
- Driver partial success: native adapter attempts local rollback, managed engine independently re-reads and restores, watchdog provides another layer.
- Wrong GPU / WARP: native bridge requires exactly one discrete AMD GPU; compute checks AMD RX model and rejects software GPU.
- Misleading measurements: simulated data labelled, scores explicit units, errors not translated into an ECC claim.
- Supply chain: native bridge is built from source, no precompiled binaries in repository.

## SOLID design choices

Single-responsibility adapters, interface-driven hardware access, immutable DTOs, isolated policy and numerical analysis, replaceable storage, independent UI. Unit tests must use simulator/fakes and remain safe on every CI runner.

## Hardware validation gates

Native ADLX API availability, ranges, write acceptance, unchanged readback, partial write rollback, GPU/DirectX adapter match, correctly reported hotspot, independent watchdog recovery, driver-reset behavior and manual Adrenalin restoration. Until verified, hardware work is EXPERIMENTAL.
