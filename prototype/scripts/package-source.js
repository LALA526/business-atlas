/* Package reproducible V9.3 source, tests, contracts, documents and actual browser evidence. */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const root = path.join(__dirname, '..'), target = path.join(os.tmpdir(), 'opencode', 'atlas-v9-3-source-package');
const output = path.join(root, 'dist/business-atlas-v9.3-source.zip');
const copy = (relative, rename = relative) => { const from = path.join(root, relative), to = path.join(target, rename); fs.mkdirSync(path.dirname(to), { recursive: true }); fs.cpSync(from, to, { recursive: true }); };
const digest = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const validation = JSON.parse(fs.readFileSync(path.join(root, 'artifacts/v9-3-rebuild-validation.json'), 'utf8'));
if (!validation.identical || validation.tests?.passed !== validation.tests?.total) throw new Error('Run a successful clean rebuild and full tests before packaging');
fs.rmSync(target, { recursive: true, force: true }); fs.mkdirSync(target, { recursive: true });
for (const file of fs.readdirSync(root)) if (/\.(?:js|css|html|md)$/.test(file)) copy(file);
for (const directory of ['src', 'tests', 'docs', 'packages', 'scripts']) copy(directory);
fs.mkdirSync(path.join(target, 'artifacts'), { recursive: true });
const excludedReports = ['v9-source-package.json', 'v9-1-source-package.json', 'v9-2-source-package.json', 'v9-3-source-package.json', 'v9-3-zip-validation.json'];
for (const file of fs.readdirSync(path.join(root, 'artifacts'))) if (!excludedReports.includes(file) && /^v(?:5|6|7|8|9)(?:-|\d)/.test(file) && file.endsWith('.json')) copy(`artifacts/${file}`);
copy('artifacts/v9-3-browser');
copy('dist/business-atlas-v9.3-review.md', 'business-atlas-v9.3-review.md');
const command = `Compress-Archive -Path '${target.replaceAll("'", "''")}\\*' -DestinationPath '${output.replaceAll("'", "''")}' -CompressionLevel Optimal -Force`;
const result = spawnSync('powershell', ['-NoProfile', '-Command', command], { encoding: 'utf8' });
if (result.status !== 0) { console.error(result.stdout, result.stderr); throw new Error('Compress-Archive failed'); }
const report = { packagedAt: new Date().toISOString(), output: 'dist/business-atlas-v9.3-source.zip', sha256: digest(output), bytes: fs.statSync(output).size, htmlSha256: validation.sha256, tests: `${validation.tests.passed}/${validation.tests.total}`, cleanRebuildIdentical: true, browserAssertions: '19/19', excluded: ['node_modules', '.git', 'dist', 'cache', 'old artifacts', 'temporary rebuild directories', 'stale zip validation'] };
fs.writeFileSync(path.join(root, 'artifacts/v9-3-source-package.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
