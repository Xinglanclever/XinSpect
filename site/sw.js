/* 曦覽官網 Service Worker：靜態資產 cache-first、HTML/JSON network-first（離線可回退）。 */
const V = 'xs-v1';

self.addEventListener('install', (e) => { self.skipWaiting(); });

self.addEventListener('activate', (e) => {
  e.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter((k) => k !== V).map((k) => caches.delete(k)));
    await self.clients.claim();
  })());
});

self.addEventListener('fetch', (e) => {
  const u = new URL(e.request.url);
  if (e.request.method !== 'GET' || u.origin !== location.origin) return;

  const isAsset = /\.(png|svg|js|css|woff2)$/.test(u.pathname);
  const isData = /changelog\.json|factkeys\.json$/.test(u.pathname);
  const isPage = e.request.mode === 'navigate' || u.pathname.endsWith('.html') || u.pathname === '/' || u.pathname === '';

  if (isAsset) {
    e.respondWith((async () => {
      const c = await caches.open(V);
      const m = await c.match(e.request);
      if (m) return m;
      const r = await fetch(e.request);
      if (r.ok) c.put(e.request, r.clone());
      return r;
    })());
  } else if (isData || isPage) {
    e.respondWith((async () => {
      const c = await caches.open(V);
      try {
        const r = await fetch(e.request);
        if (r.ok) c.put(e.request, r.clone());
        return r;
      } catch (err) {
        const m = await c.match(e.request);
        if (m) return m;
        throw err;
      }
    })());
  }
});
