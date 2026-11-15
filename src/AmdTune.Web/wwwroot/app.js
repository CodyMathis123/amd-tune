/* Strict local-only UI. All untrusted response fields are rendered via textContent. */
const $ = id => document.getElementById(id);
let securityToken = null, settings = null, capabilities = null, canWrite = false, plannedCandidates = null;
async function request(path, method = 'GET', body) {
  const options = { method, headers: {} };
  if (method !== 'GET') {
    options.headers['Content-Type'] = 'application/json';
    options.headers['X-AmdTune-CSRF'] = securityToken;
    options.body = JSON.stringify(body || {});
  }
  const response = await fetch(path, options);
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.error || 'Request failed: ' + response.status);
  return result;
}
function value(id, text) { $(id).textContent = text; }
function updateCandidate() {
  plannedCandidates = null;
  value('sweepPreview', '');
  if (!settings || !capabilities) return;
  const volt = Number($('volt').value), vram = Number($('vram').value);
  value('candidate', 'Candidate: ' + (settings.voltageOffsetMv + volt) + ' mV / ' +
    (settings.memoryClockMhz + vram) + ' MHz / current timing ' + settings.memoryTiming);
}
function displayReport(report) {
  const lines = [
    'Outcome: ' + report.phase,
    'Backend: ' + report.backend + (report.simulated ? ' (SIMULATION)' : ' (REAL HARDWARE)'),
    'Rollback verified: ' + report.rollbackVerified,
    'Verdict: ' + report.verdict,
    'Trials: ' + report.trials.length
  ];
  for (const trial of report.trials) {
    lines.push('  ' + trial.settings.voltageOffsetMv + ' mV, ' + trial.settings.memoryClockMhz +
      ' MHz: ' + trial.verdict + ' — ' + trial.reason);
    if (trial.comparisons) for (const comparison of trial.comparisons) {
      lines.push('    ' + comparison.kind + ': ' + comparison.percentChange.toFixed(2) + '% versus A/B/A reference; drift ' + comparison.referenceDriftPercent.toFixed(2) + '%');
    }
  }
  if (report.error) lines.push('Error: ' + report.error);
  value('report', lines.join('\n'));
  $('report').style.whiteSpace = 'pre-wrap';
  $('download').replaceChildren();
  const link = document.createElement('a');
  link.href = '/api/report/' + report.id;
  link.textContent = 'Download immutable audit JSON';
  link.download = report.id + '.json';
  $('download').append(link);
}
async function refreshState() {
  try {
    const status = await request('/api/experiment');
    $('start').disabled = !canWrite || status.running || !settings;
    $('sweep').disabled = !canWrite || status.running || !settings;
    $('memorySweep').disabled = !canWrite || status.running || !settings;
    $('cancel').disabled = !status.running;
    if (status.progress) {
      value('phase', status.progress.phase + ': ' + status.progress.message);
      $('bar').style.width = Math.min(100, status.progress.completedTrials /
        Math.max(1, status.progress.totalTrials) * 100) + '%';
    }
    if (status.report) displayReport(status.report);
    if (status.lastError) value('formerror', status.lastError);
  } catch (e) { value('phase', e.message); }
}
async function refreshTelemetry() {
  try {
    const t = await request('/api/telemetry');
    value('hotspot', t.hotspotC == null ? 'Unavailable' : t.hotspotC.toFixed(1) + ' °C');
    value('power', t.boardPowerW == null ? 'Unavailable' : t.boardPowerW.toFixed(0) + ' W');
    value('coreclock', t.coreClockMhz == null ? 'Unavailable' : t.coreClockMhz + ' MHz');
    value('memclock', t.memoryClockMhz == null ? 'Unavailable' : t.memoryClockMhz + ' MHz');
  } catch (e) { value('hotspot', e.message); }
}
async function init() {
  try {
    const info = await request('/api/info');
    securityToken = info.csrfToken;
    canWrite = info.canWrite;
    value('mode', info.mode.toUpperCase() + (info.canWrite ? ' • experiment enabled' : ' • READ ONLY'));
    value('notice', info.warning);
    capabilities = await request('/api/discover');
    settings = await request('/api/settings');
    value('device', JSON.stringify({ name: capabilities.name, backend: capabilities.backend,
      voltageRange: capabilities.voltageOffsetMv, memoryRange: capabilities.memoryClockMhz,
      canWriteSettings: capabilities.canWriteSettings, warnings: capabilities.warnings,
      originalSettings: settings }, null, 2));
    $('device').style.whiteSpace = 'pre-wrap';
    updateCandidate();
    await refreshTelemetry();
    await refreshState();
  } catch (e) { value('device', 'Discovery error: ' + e.message); }
}
$('volt').addEventListener('input', updateCandidate);
$('vram').addEventListener('input', updateCandidate);
$('sweep').addEventListener('click', async () => {
  try {
    const plan = await request('/api/plan');
    plannedCandidates = plan.candidates;
    value('sweepPreview', 'Candidate preview (will always restore stock):\n' +
      plannedCandidates.map(x => x.voltageOffsetMv + ' mV / ' + x.memoryClockMhz + ' MHz').join('\n'));
    value('formerror', '');
  } catch(e) { value('formerror', e.message); }
});
$('memorySweep').addEventListener('click', async () => {
  try {
    const plan = await request('/api/plan/memory');
    plannedCandidates = plan.candidates;
    value('sweepPreview', 'Conservative VRAM preview (original core voltage and timings):\n' +
      plannedCandidates.map(x => x.voltageOffsetMv + ' mV / ' + x.memoryClockMhz + ' MHz').join('\n'));
    value('formerror', '');
  } catch (e) { value('formerror', e.message); }
});
$('start').addEventListener('click', async () => {
  try {
    value('formerror', '');
    const voltageOffsetMv = settings.voltageOffsetMv + Number($('volt').value);
    const memoryClockMhz = settings.memoryClockMhz + Number($('vram').value);
    if (!Number.isInteger(voltageOffsetMv) || !Number.isInteger(memoryClockMhz))
      throw new Error('Only integer driver settings are supported.');
    await request('/api/experiment', 'POST', {
      candidates: plannedCandidates || [{ voltageOffsetMv, memoryClockMhz, memoryTiming: settings.memoryTiming }],
      repetitions: Number($('repetitions').value)
    });
    await refreshState();
  } catch (e) { value('formerror', e.message); }
});
$('cancel').addEventListener('click', async () => {
  try { await request('/api/cancel', 'POST'); await refreshState(); }
  catch (e) { value('formerror', e.message); }
});
init();
setInterval(refreshState, 1200);
setInterval(refreshTelemetry, 4000);
