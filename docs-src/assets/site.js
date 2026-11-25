/* SP COBOL Language Reference: theme switch, phone menu, search (all in the browser) */
(function () {
  var root = document.documentElement;

  // light / dark: follow the system until the reader picks one, then remember it
  var themeBtn = document.querySelector('.theme-btn');
  function current() {
    var t = root.getAttribute('data-theme');
    if (t) return t;
    return window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  if (themeBtn) themeBtn.addEventListener('click', function () {
    var next = current() === 'dark' ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem('spcobol-theme', next); } catch (e) {}
  });

  // phone menu
  var menuBtn = document.querySelector('.menu-btn');
  if (menuBtn) menuBtn.addEventListener('click', function () {
    var open = document.body.classList.toggle('menu-open');
    menuBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
  });
  var cur = document.querySelector('.side a.current');
  if (cur && cur.scrollIntoView) cur.scrollIntoView({ block: 'center' });

  // search: the index loads the first time a search box is used
  var loading = false, waiting = [];
  function load(then) {
    if (window.SPCOBOL_INDEX) return then();
    waiting.push(then);
    if (loading) return;
    loading = true;
    var s = document.createElement('script');
    s.src = 'assets/search-index.js';
    s.onload = function () { waiting.forEach(function (f) { f(); }); waiting = []; };
    document.head.appendChild(s);
  }
  function esc(s) { return s.replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function score(p, words, phrase) {
    var t = p.t.toLowerCase(), k = (p.k || '').toLowerCase(), s = (p.s || '').toLowerCase(), x = p.x.toLowerCase();
    var total = 0;
    if (t === phrase) total += 100;
    else if (t.indexOf(phrase) === 0) total += 40;
    for (var i = 0; i < words.length; i++) {
      var w = words[i], hit = 0;
      if (t.indexOf(w) >= 0) hit += 20;
      if ((' ' + k + ' ').indexOf(' ' + w) >= 0) hit += 10;
      if (s.indexOf(w) >= 0) hit += 5;
      if (x.indexOf(w) >= 0) hit += 1;
      if (!hit) return 0;  // every word must appear somewhere
      total += hit;
    }
    return total;
  }
  function wire(q, list, max) {
    var sel = -1;
    function run() {
      var phrase = q.value.trim().toLowerCase();
      sel = -1;
      if (!phrase) { list.hidden = true; list.innerHTML = ''; return; }
      var words = phrase.split(/\s+/), hits = [];
      window.SPCOBOL_INDEX.forEach(function (p) { var n = score(p, words, phrase); if (n) hits.push([n, p]); });
      hits.sort(function (a, b) { return b[0] - a[0]; });
      list.innerHTML = hits.length ? hits.slice(0, max).map(function (h) {
        return '<li><a href="' + h[1].u + '"><b>' + esc(h[1].t) + '</b> <small>' + esc(h[1].c) + ' &middot; ' + esc(h[1].s || '') + '</small></a></li>';
      }).join('') : '<li class="none">Nothing found. Try a statement name, like MOVE or PERFORM.</li>';
      list.hidden = false;
    }
    q.addEventListener('input', function () { load(run); });
    q.addEventListener('focus', function () { load(function () { if (q.value) run(); }); });
    q.addEventListener('keydown', function (e) {
      var links = list.querySelectorAll('a');
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        if (!links.length) return;
        e.preventDefault();
        sel = (sel + (e.key === 'ArrowDown' ? 1 : -1) + links.length) % links.length;
        for (var i = 0; i < links.length; i++) links[i].classList.toggle('sel', i === sel);
      } else if (e.key === 'Enter') {
        var a = links[sel >= 0 ? sel : 0];
        if (a) location.href = a.getAttribute('href');
      } else if (e.key === 'Escape') {
        list.hidden = true;
      }
    });
    return run;
  }
  var q = document.getElementById('q'), list = document.getElementById('results');
  if (!q || !list) return;
  wire(q, list, 15);
  document.addEventListener('click', function (e) { if (!e.target.closest('.search')) list.hidden = true; });
  var q2 = document.getElementById('q2'), list2 = document.getElementById('results2');
  var run2 = q2 && list2 ? wire(q2, list2, 30) : null;
  document.addEventListener('keydown', function (e) {
    if (e.key === '/' && !/input|textarea/i.test(document.activeElement.tagName)) { e.preventDefault(); (q2 || q).focus(); }
  });
  var m = /[?&]q=([^&]*)/.exec(location.search);
  if (m) {
    var box = q2 || q;
    box.value = decodeURIComponent(m[1].replace(/\+/g, ' '));
    box.focus();
    box.dispatchEvent(new Event('input'));
  }
})();
