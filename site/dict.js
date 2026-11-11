/* 曦覽官網：事實辭典 —— 點事實域片彈出該域全部鍵，可篩選、可複製。
   資料：factkeys.json（由 Tools/gen-site-data.py 自 FactKeyCatalog.cs 生成）。 */
(function () {
  'use strict';
  var DATA = null, dlg = null, listEl = null, filterEl = null, titleEl = null, countEl = null, current = [];

  function label(zh, en) { return document.documentElement.lang === 'en' ? en : zh; }
  function esc(s) { return s.replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  function ensureData() {
    if (DATA) return Promise.resolve(DATA);
    return fetch('factkeys.json').then(function (r) { if (!r.ok) throw 0; return r.json(); })
      .then(function (d) { DATA = d; return d; });
  }

  function render() {
    var q = (filterEl.value || '').trim().toLowerCase();
    var rows = current.filter(function (k) { return !q || k.indexOf(q) > -1; });
    listEl.innerHTML = rows.map(function (k) {
      return '<div class="krow"><code>' + esc(k) + '</code>' +
        '<button type="button" class="copyBtn" data-k="' + esc(k) + '">' + label('複製', 'Copy') + '</button></div>';
    }).join('') || '<p class="knone">' + label('沒有符合的鍵。', 'No matching keys.') + '</p>';
    countEl.textContent = rows.length + ' / ' + current.length;
  }

  function show(keys, title) {
    current = keys;
    titleEl.textContent = title;
    filterEl.value = '';
    render();
    dlg.showModal();
    filterEl.focus();
  }

  function openDomain(d) {
    ensureData().then(function (data) {
      var dd = data.domains.find(function (x) { return x.d === d; });
      if (dd) show(dd.keys, label('域 ', 'Domain ') + d + ' ・ ' + dd.n + label(' 把鍵', ' keys'));
    }).catch(function () { });
  }

  function openAll() {
    ensureData().then(function (data) {
      var all = [];
      data.domains.forEach(function (x) { all = all.concat(x.keys); });
      show(all, label('全部域', 'All domains') + ' ・ ' + all.length + label(' 把鍵', ' keys'));
    }).catch(function () { });
  }

  function init() {
    dlg = document.getElementById('keydlg');
    if (!dlg) return;
    listEl = document.getElementById('keylist');
    filterEl = document.getElementById('keyfilter');
    titleEl = document.getElementById('keytitle');
    countEl = document.getElementById('keycount');

    document.querySelectorAll('.dom span').forEach(function (s) {
      var code = s.querySelector('code');
      if (!code) return;
      var dname = code.textContent;
      s.setAttribute('role', 'button');
      s.tabIndex = 0;
      s.addEventListener('click', function () { openDomain(dname); });
      s.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openDomain(dname); }
      });
    });

    var allBtn = document.getElementById('keyall');
    if (allBtn) allBtn.addEventListener('click', openAll);
    filterEl.addEventListener('input', render);
    var closeBtn = document.getElementById('keyclose');
    if (closeBtn) closeBtn.addEventListener('click', function () { dlg.close(); });
    dlg.addEventListener('click', function (e) { if (e.target === dlg) dlg.close(); });
    listEl.addEventListener('click', function (e) {
      var b = e.target.closest('.copyBtn');
      if (!b) return;
      var k = b.getAttribute('data-k');
      navigator.clipboard.writeText(k).then(function () {
        b.textContent = '✓';
        setTimeout(function () { b.textContent = label('複製', 'Copy'); }, 1200);
      }).catch(function () { b.textContent = '✕'; });
    });
  }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init); }
  else { init(); }
})();
