/* Rebuild V9.3 in a clean directory, run every suite and compare SHA-256 with delivered HTML. */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const root = path.join(__dirname, '..');
const target = process.env.ATLAS_REBUILD_DIR || path.join(os.tmpdir(), 'opencode', 'atlas-v9-3-clean-rebuild');
const copy = relative => { const from = path.join(root, relative), to = path.join(target, relative); fs.mkdirSync(path.dirname(to), { recursive: true }); fs.cpSync(from, to, { recursive: true }); };
const digest = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
fs.rmSync(target, { recursive: true, force: true }); fs.mkdirSync(target, { recursive: true });
for (const file of fs.readdirSync(root)) if (/\.(?:js|css|html|md)$/.test(file)) copy(file);
for (const directory of ['src', 'tests', 'docs', 'packages', 'scripts']) copy(directory);
fs.mkdirSync(path.join(target, 'artifacts'), { recursive: true });
const run = (command, args, env = {}) => { const result = spawnSync(command, args, { cwd: target, env: { ...process.env, ATLAS_BUILD_TIME: '2026-11-15T00:00:00.000Z', ...env }, encoding: 'utf8', timeout: 180000 }); if (result.status !== 0) { console.error(result.stdout); console.error(result.stderr); throw new Error(`${command} ${args.join(' ')} failed in clean rebuild`); } return result.stdout.trim(); };
const outputs = [], suiteResults = {};
try {
  outputs.push(run(process.execPath, ['scripts/extract-self-knowledge.js']));
  outputs.push(run(process.execPath, ['scripts/prepare-self-project.js']));
  outputs.push(run(process.execPath, ['build.js']));
  const original = path.join(root, 'dist/business-atlas-v9.3.html'), rebuilt = path.join(target, 'dist/business-atlas-v9.3.html'), originalSha = digest(original), rebuiltSha = digest(rebuilt);
  if (originalSha !== rebuiltSha) throw new Error(`SHA-256 mismatch: ${originalSha} vs ${rebuiltSha}`);
  const suites = ['acceptance.test.js', 'v4-acceptance.test.js', 'tests/v5-acceptance.test.js', 'tests/v6-acceptance.test.js', 'tests/v7-acceptance.test.js', 'tests/v8-acceptance.test.js', 'tests/v9-acceptance.test.js', 'tests/v9-1-acceptance.test.js', 'tests/v9-2-acceptance.test.js', 'tests/v9-3-acceptance.test.js'];
  for (const suite of suites) {
    const output = run(process.execPath, [suite], { ATLAS_SINGLE_HTML: 'dist/business-atlas-v9.3.html' }); outputs.push(output.split(/\r?\n/).filter(line => /\d+\/\d+/.test(line)).slice(-2).join('\n'));
    const summaries = [...output.matchAll(/(\d+)\/(\d+)\s*项(?:\s+[^\n]*?)?通过/g)].map(match => ({ passed: Number(match[1]), total: Number(match[2]) }));
    const summary = summaries.at(-1); if (!summary || summary.passed !== summary.total) throw new Error(`Cannot confirm complete test success for ${suite}`);
    suiteResults[suite] = summary;
  }
  const passed = Object.values(suiteResults).reduce((sum, item) => sum + item.passed, 0), total = Object.values(suiteResults).reduce((sum, item) => sum + item.total, 0);
  const report = { validatedAt: new Date().toISOString(), cleanDirectory: target, original: 'dist/business-atlas-v9.3.html', rebuilt: 'dist/business-atlas-v9.3.html', sha256: originalSha, identical: true, tests: { suites: suiteResults, legacyPassed: passed - suiteResults['tests/v9-3-acceptance.test.js'].passed, legacyTotal: total - suiteResults['tests/v9-3-acceptance.test.js'].total, v93: suiteResults['tests/v9-3-acceptance.test.js'], passed, total }, externalResources: 0, outputs };
  fs.writeFileSync(path.join(root, 'artifacts/v9-3-rebuild-validation.json'), JSON.stringify(report, null, 2));
  fs.writeFileSync(path.join(target, 'artifacts/v9-3-rebuild-validation.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ identical: true, sha256: originalSha, tests: `${passed}/${total}`, cleanDirectory: target }, null, 2));
} catch (error) {
  fs.writeFileSync(path.join(root, 'artifacts/v9-3-rebuild-validation.json'), JSON.stringify({ validatedAt: new Date().toISOString(), cleanDirectory: target, identical: false, error: error.message, tests: { suites: suiteResults }, outputs }, null, 2));
  console.error(error.stack); process.exitCode = 1;
}
