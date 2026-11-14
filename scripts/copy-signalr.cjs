const fs = require('node:fs');
const path = require('node:path');
const source = require.resolve('@microsoft/signalr/dist/browser/signalr.min.js');
const target = path.resolve(__dirname, '../wwwroot/js/signalr.min.js');
fs.copyFileSync(source, target);
