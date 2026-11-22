/* Build the current lightweight V9.4 runtime into one offline HTML file. */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = __dirname, read = file => fs.readFileSync(path.join(root, file), 'utf8');
const files = [
  'v4-core.js', 'src/v5-core.js', 'src/project-factory.js', 'src/v9-applicability.js', 'src/write-access.js',
  'src/v6-core.js', 'src/v6-projection.js', 'src/v7-framework.js', 'src/v8-read.js', 'src/v9-access.js',
  'src/v9-model.js', 'src/decision.js', 'src/demo-model.js', 'src/v9-akp.js', 'src/v9-services.js',
  'src/ui-utils.js', 'src/knowledge-reading.js', 'src/task.js', 'src/app-pages.js', 'fixtures/demo-projects.js', 'src/v6-app.js'
];
const scripts = files.map(file => `<script data-source="${file}">\n${read(file).replace(/<\/script/gi, '<\\/script')}\n</script>`).join('\n');
const html = read('src/index.template.html')
  .replace('/* INLINE_CSS */', () => read('src/app.css'))
  .replace('<!-- INLINE_SCRIPTS -->', () => scripts);
fs.mkdirSync(path.join(root, 'dist'), { recursive: true });
const output = path.join(root, 'dist', 'business-atlas-v9.4.html');
fs.writeFileSync(output, html);
if (/<(?:script|link|img)[^>]+(?:src|href)=["'](?:https?:)?\/\//i.test(html) || /@import\s|url\(["']?https?:/i.test(html)) throw new Error('Single HTML contains an external resource reference');
if (/<svg\b|<canvas\b|localDiagram|detailedFlowGraph|stageGraph|stateGraph/i.test(html)) throw new Error('Active visual drawing runtime detected');
const digest = crypto.createHash('sha256').update(html).digest('hex');
fs.mkdirSync(path.join(root, 'artifacts'), { recursive: true });
fs.writeFileSync(path.join(root, 'artifacts/v9-4-build.json'), JSON.stringify({ output: path.relative(root, output), sha256: digest, bytes: Buffer.byteLength(html), scripts: files, styles: ['src/app.css'] }, null, 2));
console.log(`Built ${path.relative(root, output)} · ${(Buffer.byteLength(html) / 1024).toFixed(1)} KiB · ${files.length} scripts · 1 stylesheet · no external resources · ${digest}`);
