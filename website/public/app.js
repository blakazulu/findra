/* Findra site. No dependencies, no analytics, nothing that phones anywhere -
   which would be an odd thing for this page of all pages to do. */

(function () {
  'use strict';

  var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* ---- the joke that keeps running: Windows' clock never stops ---------- */

  var elapsed = document.getElementById('elapsed');
  var scanned = document.getElementById('scanned');

  if (elapsed && scanned && !reduced) {
    var seconds = 252;
    setInterval(function () {
      seconds += 1;
      var m = Math.floor(seconds / 60);
      var s = seconds % 60;
      elapsed.textContent = m + ' min ' + (s < 10 ? '0' + s : s) + ' s';
      scanned.textContent = (1842910 + seconds * 7).toLocaleString('en-US');
    }, 1000);
  }

  /* ---- ticker ---------------------------------------------------------- */

  var ticker = document.getElementById('ticker');
  if (ticker) {
    var lines = [
      'SEARCH INDEX REBUILD SCHEDULED FOR 3 AM, AGAIN',
      'ONE FILE FOUND, IT WAS THE SHORTCUT',
      'THE FILE WAS IN DOWNLOADS THE ENTIRE TIME',
      'NVMe AT 100% ACTIVE TIME FOR SIX MINUTES',
      'FINDRA: 0.5 MS, NO INDEX REBUILD, NO 3 AM'
    ];
    var html = lines.map(function (line) {
      return '<span>' + line + '</span><span class="d">&#9670;</span>';
    }).join('');
    ticker.innerHTML = html + html;
  }

  /* ---- click a command, get it on the clipboard ------------------------ */

  document.querySelectorAll('.cmd[data-copy]').forEach(function (el) {
    var label = el.querySelector('.c');
    var original = label ? label.textContent : '';

    el.addEventListener('click', function () {
      var text = el.getAttribute('data-copy');
      if (!navigator.clipboard) return;

      navigator.clipboard.writeText(text).then(function () {
        if (!label) return;
        el.classList.add('copied');
        label.textContent = 'COPIED';
        setTimeout(function () {
          el.classList.remove('copied');
          label.textContent = original;
        }, 1600);
      }).catch(function () {
        /* a browser that says no is not an error worth shouting about */
      });
    });
  });

  /* ---- reveal on scroll ------------------------------------------------ */

  var targets = document.querySelectorAll('.reveal');

  if (reduced || !('IntersectionObserver' in window)) {
    targets.forEach(function (el) { el.classList.add('in'); });
  } else {
    var seen = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        entry.target.classList.add('in');
        seen.unobserve(entry.target);
      });
    }, { rootMargin: '0px 0px -12% 0px', threshold: 0.08 });

    targets.forEach(function (el, i) {
      el.style.transitionDelay = (i % 4) * 70 + 'ms';
      seen.observe(el);
    });
  }

  /* ---- footer year ----------------------------------------------------- */

  var year = document.getElementById('year');
  if (year) year.textContent = String(new Date().getFullYear());
})();
