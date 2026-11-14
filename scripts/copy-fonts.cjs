const fs = require('node:fs');
const path = require('node:path');
const cssPath = path.resolve(__dirname, '../wwwroot/css/app.min.css');
const css = fs.readFileSync(cssPath, 'utf8');
const assetRoots = [
  path.resolve(__dirname, '../node_modules/@fontsource-variable/inter/files'),
  path.resolve(__dirname, '../node_modules/@fontsource-variable/noto-sans-sinhala/files'),
  path.resolve(__dirname, '../node_modules/material-symbols')
];
for (const [, rawUrl] of css.matchAll(/url\((?:"|')?([^"')]+)(?:"|')?\)/g)) {
  if (/^(data:|https?:|\/\/)/i.test(rawUrl)) continue;
  const relative = rawUrl.replace(/^\.\//, '');
  const fileName = path.basename(relative);
  const source = assetRoots.map(root => path.join(root, fileName)).find(file => fs.existsSync(file));
  if (!source) throw new Error(`Missing referenced font asset: ${rawUrl}`);
  const destination = path.resolve(path.dirname(cssPath), relative);
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  fs.copyFileSync(source, destination);
}
