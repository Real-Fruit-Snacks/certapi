// certapi project page: theme switch, copy buttons, and the seal's two runs.
(function () {
  var root = document.documentElement;
  var still = matchMedia('(prefers-reduced-motion: reduce)');
  var dark = matchMedia('(prefers-color-scheme: dark)');

  // Theme: the OS decides until the switch is used; the choice is saved as rfs-theme.
  var themeBtn = document.getElementById('theme');
  function current() { return root.dataset.theme || (dark.matches ? 'nightshift' : 'dayshift'); }
  themeBtn.addEventListener('click', function (e) {
    var next = current() === 'nightshift' ? 'dayshift' : 'nightshift';
    var apply = function () { root.dataset.theme = next; try { localStorage.setItem('rfs-theme', next); } catch (err) {} };
    if (!document.startViewTransition || still.matches) return apply();
    var x = e.clientX || innerWidth - 40, y = e.clientY || 32;
    var r = Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y));
    document.startViewTransition(apply).ready.then(function () {
      root.animate({ clipPath: ['circle(0 at ' + x + 'px ' + y + 'px)', 'circle(' + r + 'px at ' + x + 'px ' + y + 'px)'] },
        { duration: 520, easing: 'cubic-bezier(.2,.7,.2,1)', pseudoElement: '::view-transition-new(root)' });
    });
  });

  // Copy buttons
  document.querySelectorAll('.copy').forEach(function (b) {
    b.addEventListener('click', function () {
      if (!navigator.clipboard) return;
      navigator.clipboard.writeText(b.dataset.copy).then(function () {
        b.textContent = 'Copied';
        setTimeout(function () { b.textContent = 'Copy'; }, 1600);
      });
    });
  });

  // The seal: replay the run with the certificate, or the same URL without one.
  var seal = document.getElementById('seal');
  var runs = document.getElementById('runs');
  if (!seal || !runs) return;
  runs.hidden = false;
  runs.querySelectorAll('button').forEach(function (b) {
    b.addEventListener('click', function () {
      seal.dataset.run = b.dataset.run;
      runs.querySelectorAll('button').forEach(function (o) { o.setAttribute('aria-pressed', String(o === b)); });
      seal.classList.remove('play');
      void seal.offsetWidth;          // restart the CSS animation
      seal.classList.add('play');
    });
  });

  // On a desktop pointer the rings turn a few degrees against each other, like a rose engine,
  // and the engraving shimmers over the uncut plate beneath it.
  var art = seal.querySelector('.seal-art');
  if (!art || still.matches || !matchMedia('(hover: hover) and (pointer: fine)').matches) return;
  var hero = document.querySelector('.hero'), raf = 0, turn = 0;
  hero.addEventListener('pointermove', function (e) {
    var r = art.getBoundingClientRect();
    turn = Math.max(-1, Math.min(1, (e.clientX - (r.left + r.width / 2)) / (r.width))) * 6;
    if (!raf) raf = requestAnimationFrame(function () { art.style.setProperty('--turn', turn.toFixed(2) + 'deg'); raf = 0; });
  });
  hero.addEventListener('pointerleave', function () { art.style.setProperty('--turn', '0deg'); });
})();
