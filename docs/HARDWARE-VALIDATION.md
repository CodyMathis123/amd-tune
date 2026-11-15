# RX 9070 XT hardware validation runbook

This checklist is directly executable on a Windows machine and must be performed **manually**. It cannot be substituted by simulator CI.

## Gate 0 — prerequisite checks (no writes)

1. Ensure GPU clocks/voltages are at known Adrenalin defaults. Export a separate backup of your current tuning profile.
2. Confirm AMD ADLX 2.0 SDK code is from GPUOpen; use Visual Studio x64 Developer PowerShell, .NET 10 SDK.
3. Build and run:

       ./scripts/build-native.ps1 -AdlxSdkDirectory ./third_party/ADLX
       ./scripts/publish-windows.ps1
       ./scripts/hardware-doctor.ps1 | Tee-Object -FilePath artifacts/hardware-doctor.log

4. Confirm the name identifies **your RX 9070 XT**, not an iGPU or different adapter. Confirm driver-reported VoltageOffsetMv/MemoryClockMhz ranges and explicit Fast Timing support. Confirm hotspot is plausible.
5. Launch in read-only mode. Check that there are no tuning changes after navigation or refresh:

       ./artifacts/app/AmdTune.Web.exe --hardware

## Gate 1 — conservative first write (will modify hardware)

Only proceed after verifying Gate 0. Stop other tuning tools (Adrenalin's manual profile editor, MSI Afterburner, etc.). Save important work. Real GPU instability may reset the display driver or system.

1. Start the exact command:

       ./artifacts/app/AmdTune.Web.exe --hardware --enable-hardware-writes

2. In the UI: core offset -15 mV relative to current; VRAM +0 MHz; timing unchanged; repetitions=3.
3. Verify the recovery process starts **before** ADLX applies a candidate, status shows a matching driver readback, all tests run, and original voltage/VRAM/timing are restored.
4. Independently confirm original values in AMD Adrenalin after the test. Check the JSON audit in %LOCALAPPDATA%/AmdTune/reports.
5. Use a separate monitoring tool to compare GPU clocks, hotspot and board power.

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
