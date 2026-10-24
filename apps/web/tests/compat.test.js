const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const { parse } = require('acorn');

const root = path.join(__dirname, '..');
const source = fs.readFileSync(path.join(root, 'src/compat.js'), 'utf8');

function loadCompat(hasFlexGap) {
  const classes = new Set();
  const children = [];
  const context = {
    window: {},
    document: {
      createElement: () => ({ style: {}, appendChild() {}, scrollHeight: hasFlexGap ? 1 : 0 }),
      body: {
        appendChild(node) { children.push(node); },
        removeChild(node) { children.splice(children.indexOf(node), 1); }
      },
      documentElement: { classList: { toggle(name, on) { if (on) classes.add(name); else classes.delete(name); } } }
    }
  };
  vm.runInNewContext(source, context);
  return { compat: context.window.StreamedCompat, classes, children };
}

test('older flex layout activates fallback and removes its measurement element', () => {
  const { compat, classes, children } = loadCompat(false);
  assert.equal(compat.hasFlexGap, false);
  assert.equal(classes.has('no-flex-gap'), true);
  assert.equal(children.length, 0);
});

test('modern browsers retain native flex spacing', () => {
  const { compat, classes, children } = loadCompat(true);
  assert.equal(compat.hasFlexGap, true);
  assert.equal(classes.has('no-flex-gap'), false);
  assert.equal(children.length, 0);
});

test('Samsung vendor names do not hide numeric Return, media, or color codes', () => {
  const { compat } = loadCompat(false);
  for (const code of [10009, 403, 405, 406, 415, 19, 10252, 417, 412]) {
    for (const key of ['Unidentified', 'Unknown', 'XF86Back', 'VendorKey', '']) {
      assert.equal(compat.getKey({ key, keyCode: code }), code);
    }
  }
});

test('legacy arrows, numeric-only remotes, and desktop typing keep their meaning', () => {
  const { compat } = loadCompat(false);
  assert.equal(compat.getKey({ key: 'Left', keyCode: 37 }), 'ArrowLeft');
  assert.equal(compat.getKey({ key: 'Esc', keyCode: 27 }), 'Escape');
  assert.equal(compat.getKey({ key: 'XF86Back' }), 'Escape');
  assert.equal(compat.getKey({ key: 'Unidentified', keyCode: 13 }), 13);
  assert.equal(compat.getKey({ which: 39 }), 39);
  assert.equal(compat.getKey({ key: 'ArrowDown', keyCode: 40 }), 'ArrowDown');
  assert.equal(compat.getKey({ key: 'a', keyCode: 65 }), 'a');
});

test('LG Magic Remote Back retains its meaning with vendor key names', () => {
  const { compat } = loadCompat(true);
  for (const key of ['Unidentified', 'VendorKey', 'GoBack', 'Back', '']) {
    assert.equal(compat.getKey({ key, keyCode: 461 }), 'Escape');
  }
});

test('browser scripts parse within the Chromium 69 JavaScript syntax baseline', () => {
  for (const name of ['src/compat.js', 'src/api.js', 'src/app.js', 'data/team-db.js', 'data/config.example.js']) {
    parse(fs.readFileSync(path.join(root, name), 'utf8'), { ecmaVersion: 2018 });
  }
});

test('TV packaging includes compatibility assets and remote-key privilege', () => {
  const project = fs.readFileSync(path.join(root, 'tizen_web_project.yaml'), 'utf8');
  const manifest = fs.readFileSync(path.join(root, 'config.xml'), 'utf8');
  assert.match(project, /- src\/compat\.js/);
  assert.match(project, /- src\/compat\.css/);
  assert.match(manifest, /http:\/\/tizen\.org\/privilege\/tv\.inputdevice/);
});
