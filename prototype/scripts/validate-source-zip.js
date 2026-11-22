/* Extract the actual V9.3 source ZIP, rebuild it, run every suite and compare delivery HTML. */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const root = path.join(__dirname, '..'), zip = path.join(root, 'dist/business-atlas-v9.3-source.zip');
const target = process.env.ATLAS_ZIP_CHECK_DIR || path.join(os.tmpdir(), 'opencode', 'atlas-v9-3-zip-check');
const digest = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
fs.rmSync(target, { recursive: true, force: true }); fs.mkdirSync(target, { recursive: true });
const unpacked = spawnSync('powershell', ['-NoProfile', '-Command', `Expand-Archive -LiteralPath '${zip.replaceAll("'", "''")}' -DestinationPath '${target.replaceAll("'", "''")}' -Force`], { encoding: 'utf8' });
if (unpacked.status !== 0) { console.error(unpacked.stdout, unpacked.stderr); throw new Error('Cannot extract source ZIP'); }
const forbidden = [];
function inspect(directory) { for (const entry of fs.readdirSync(directory, { withFileTypes: true })) { const full = path.join(directory, entry.name); if (['.git', 'node_modules'].includes(entry.name)) forbidden.push(full); else if (entry.isDirectory()) inspect(full); } }
inspect(target);
if (forbidden.length) throw new Error(`Forbidden ZIP content: ${forbidden.join(', ')}`);
for (const step of [['scripts/extract-self-knowledge.js'], ['scripts/prepare-self-project.js'], ['build.js']]) {
  const prepared = spawnSync(process.execPath, step, { cwd: target, env: { ...process.env, ATLAS_BUILD_TIME: '2026-11-15T00:00:00.000Z' }, encoding: 'utf8', timeout: 180000 });
  console.log(prepared.stdout); if (prepared.status !== 0) { console.error(prepared.stderr); throw new Error(`Source ZIP prepare step failed: ${step[0]}`); }
}
const run = spawnSync(process.execPath, ['scripts/validate-rebuild.js'], { cwd: target, env: { ...process.env, ATLAS_REBUILD_DIR: path.join(os.tmpdir(), 'opencode', 'atlas-v9-3-zip-rebuild') }, encoding: 'utf8', timeout: 600000 });
console.log(run.stdout); if (run.status !== 0) { console.error(run.stderr); throw new Error('Source ZIP rebuild or tests failed'); }
const rebuiltValidation = JSON.parse(fs.readFileSync(path.join(target, 'artifacts/v9-3-rebuild-validation.json'), 'utf8')), deliveredSha = digest(path.join(root, 'dist/business-atlas-v9.3.html')), zipSha = digest(zip);
if (deliveredSha !== rebuiltValidation.sha256) throw new Error(`ZIP HTML differs from delivery: ${deliveredSha} vs ${rebuiltValidation.sha256}`);
const required = ['src/v9-3-decision.js', 'src/v9-3-reading.js', 'src/v9-3-task.js', 'src/v9-3-ui.js', 'tests/v9-3-acceptance.test.js', 'docs/v9-3-business-decision-model.md', 'docs/v9-3-task-semantic-contract.md', 'docs/v9-3-impact-and-delivery.md', 'docs/v9-3-migration-compatibility.md', 'business-atlas-v9.3-review.md', 'artifacts/v9-3-browser-results.json'];
const missing = required.filter(file => !fs.existsSync(path.join(target, file))); if (missing.length) throw new Error(`ZIP is missing required files: ${missing.join(', ')}`);
const screenshots = fs.readdirSync(path.join(target, 'artifacts/v9-3-browser')).filter(file => file.endsWith('.png')).length;
const report = { validatedAt: new Date().toISOString(), zip: 'dist/business-atlas-v9.3-source.zip', zipSha256: zipSha, extractedDirectory: target, actualExtraction: true, htmlSha256: deliveredSha, htmlIdentical: true, tests: `${rebuiltValidation.tests.passed}/${rebuiltValidation.tests.total}`, screenshots, requiredFilesPresent: required.length, forbiddenEntries: forbidden, browserEvidence: 'artifacts/v9-3-browser-results.json' };
fs.writeFileSync(path.join(root, 'artifacts/v9-3-zip-validation.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
