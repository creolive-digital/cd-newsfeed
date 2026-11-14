const fs = require('node:fs');
const path = require('node:path');
const packageEntry = require.resolve('@microsoft/signalr');
const packageRoot = path.resolve(path.dirname(packageEntry), '../..');
const browserDirectory = path.join(packageRoot, 'dist/browser');
const minifiedSource = path.join(browserDirectory, 'signalr.min.js');
const source = fs.existsSync(minifiedSource)
  ? minifiedSource
  : path.join(browserDirectory, 'signalr.js');
const target = path.resolve(__dirname, '../wwwroot/js/signalr.min.js');
fs.mkdirSync(path.dirname(target), { recursive: true });
fs.copyFileSync(source, target);
