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
