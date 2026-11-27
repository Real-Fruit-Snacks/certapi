// certapi project page: Cyclorama Dawn v2.1.0 theme switch (Dark LX 263 default),
// copy buttons with semantic toasts, and the guilloche seal's two runs.
(function () {
  var root = document.documentElement;
  var still = matchMedia('(prefers-reduced-motion: reduce)');
  var $ = function (id) { return document.getElementById(id); };

  // ---- theme (Dark LX 263 default; Light LX 512 opt-in) ----
  function currentTheme() { return root.dataset.theme === 'light' ? 'light' : 'dark'; }
  function applyTheme(next) {
    if (next === 'light') root.dataset.theme = 'light';
    else delete root.dataset.theme;
    var isDark = currentTheme() === 'dark';
    var cs = document.querySelector('meta[name="color-scheme"]');
    if (cs) cs.content = isDark ? 'dark' : 'light';
    var tc = document.querySelector('meta[name="theme-color"]');
    if (tc) tc.content = isDark ? '#050507' : '#F5F7F9';
    var btn = $('theme');
    if (btn) btn.setAttribute('aria-label', isDark ? 'Switch to Alabaster Day light mode' : 'Switch to Beacon Horizon dark mode');
  }
  $('theme').addEventListener('click', function (e) {
    var next = currentTheme() === 'dark' ? 'light' : 'dark';
    var flip = function () {
      applyTheme(next);
      try {
        localStorage.setItem('rfs-theme', next);
        localStorage.setItem('color-scheme', next);
      } catch (err) {}
    };
    if (!document.startViewTransition || still.matches) return flip();
    var br = $('theme').getBoundingClientRect();
    var x = e.clientX || (br.left + br.width / 2);
    var y = e.clientY || (br.top + br.height / 2);
    var r = Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y));
    var vt = document.startViewTransition(flip);
    var animated = vt.ready.then(function () {
      return root.animate(
        { clipPath: ['circle(0 at ' + x + 'px ' + y + 'px)', 'circle(' + r + 'px at ' + x + 'px ' + y + 'px)'] },
        { duration: 520, easing: 'cubic-bezier(.2,.7,.2,1)', pseudoElement: '::view-transition-new(root)' }
      ).finished;
    });
    Promise.allSettled([animated, vt.updateCallbackDone, vt.finished]);
  });
  window.addEventListener('storage', function (e) {
    if (e.key === 'rfs-theme' || e.key === 'color-scheme') {
      applyTheme(e.newValue === 'light' || e.newValue === 'dayshift' ? 'light' : 'dark');
    }
  });
  applyTheme(currentTheme());

  // ---- semantic toast notification ----
  var toastRegion = $('toast-region');
  var toastSr = $('toast-sr');
  function showToast(kind, tag, message) {
    if (typeof document.ariaNotify === 'function') {
      try { document.ariaNotify(message); } catch (e) {}
    } else if (toastSr) {
      toastSr.textContent = '';
      toastSr.textContent = message;
    }
    if (!toastRegion) return;
    if (typeof toastRegion.showPopover === 'function') {
      try { if (!toastRegion.matches(':popover-open')) toastRegion.showPopover(); } catch (e) {}
    }
    var item = document.createElement('div');
    item.className = 'rfs-toast is-' + kind;
    var msg = document.createElement('span');
    msg.className = 'rfs-toast-msg';
    var k = document.createElement('span');
    k.className = 'k';
    k.textContent = tag + ' ';
    msg.appendChild(k);
    msg.appendChild(document.createTextNode(message));
    var close = document.createElement('button');
    close.type = 'button';
    close.className = 'rfs-toast-close';
    close.setAttribute('aria-label', 'Dismiss notification');
    close.textContent = '×';
    item.appendChild(msg);
    item.appendChild(close);
    toastRegion.appendChild(item);

    var remove = function () {
      if (item.parentNode) item.parentNode.removeChild(item);
      if (toastRegion.childElementCount === 0 && typeof toastRegion.hidePopover === 'function') {
        try { toastRegion.hidePopover(); } catch (e) {}
      }
    };
    close.addEventListener('click', remove);
    setTimeout(function tick() {
      if (item.contains(document.activeElement)) return setTimeout(tick, 1200);
      remove();
    }, 2600);
  }

  // ---- copy buttons ----
  function fallbackCopy(text) {
    try {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.style.cssText = 'position:fixed;left:-9999px;top:0;opacity:0;';
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand('copy');
      ta.remove();
      return ok;
    } catch (err) {
      return false;
    }
  }
  document.querySelectorAll('.copy').forEach(function (b) {
    var label = b.textContent;
    b.addEventListener('click', function () {
      var done = function (ok) {
        b.textContent = ok ? '[ok] Copied' : 'Select text';
        showToast(ok ? 'ok' : 'warn', ok ? '[ok]' : '[!]', ok ? 'Command copied to clipboard.' : 'Copy manually from the code block.');
        clearTimeout(b._t);
        b._t = setTimeout(function () { b.textContent = label; }, 1800);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(b.dataset.copy).then(function () { done(true); }, function () { done(fallbackCopy(b.dataset.copy)); });
      } else {
        done(fallbackCopy(b.dataset.copy));
      }
    });
  });

  // ---- the guilloche seal ----
  var seal = $('seal');
  var runs = $('runs');
  if (!seal || !runs) return;
  runs.hidden = false;
  runs.querySelectorAll('button').forEach(function (b) {
    b.addEventListener('click', function () {
      seal.dataset.run = b.dataset.run;
      runs.querySelectorAll('button').forEach(function (o) { o.setAttribute('aria-pressed', String(o === b)); });
      seal.classList.remove('play');
      void seal.offsetWidth;
      seal.classList.add('play');
    });
  });

  var art = seal.querySelector('.seal-art');
  if (!art || still.matches || !matchMedia('(hover: hover) and (pointer: fine)').matches) return;
  var hero = document.querySelector('.hero'), raf = 0, turn = 0;
  hero.addEventListener('pointermove', function (e) {
    var r = art.getBoundingClientRect();
    turn = Math.max(-1, Math.min(1, (e.clientX - (r.left + r.width / 2)) / r.width)) * 6;
    if (!raf) raf = requestAnimationFrame(function () { art.style.setProperty('--turn', turn.toFixed(2) + 'deg'); raf = 0; });
  });
  hero.addEventListener('pointerleave', function () { art.style.setProperty('--turn', '0deg'); });
})();
