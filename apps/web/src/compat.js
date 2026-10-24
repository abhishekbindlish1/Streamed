(function () {
  'use strict';

  // Chromium 69 accepts gap for Grid but ignores it for Flexbox. CSS.supports
  // therefore cannot distinguish the two; measure a real flex layout instead.
  var probe = document.createElement('div');
  probe.style.cssText = 'position:absolute;visibility:hidden;display:flex;flex-direction:column;row-gap:1px;padding:0;border:0;';
  probe.appendChild(document.createElement('div'));
  probe.appendChild(document.createElement('div'));
  document.body.appendChild(probe);
  var hasFlexGap = probe.scrollHeight === 1;
  document.body.removeChild(probe);
  document.documentElement.classList.toggle('no-flex-gap', !hasFlexGap);

  function getKey(event) {
    var code = event.keyCode || event.which;
    // Samsung may provide a vendor-specific key name (or "Unidentified") even
    // though its numeric Return / media / color key code is valid.
    if ([10009, 403, 405, 406, 415, 19, 10252, 417, 412].indexOf(code) >= 0) return code;
    var key = event.key;
    if (!key || key === 'Unidentified' || key === 'Unknown') return code;
    var aliases = {
      Left: 'ArrowLeft', Right: 'ArrowRight', Up: 'ArrowUp', Down: 'ArrowDown',
      Esc: 'Escape', Return: 'Enter', Back: 'Escape', GoBack: 'Escape', XF86Back: 'Escape'
    };
    return Object.prototype.hasOwnProperty.call(aliases, key) ? aliases[key] : key;
  }

  window.StreamedCompat = { hasFlexGap: hasFlexGap, getKey: getKey };
})();
