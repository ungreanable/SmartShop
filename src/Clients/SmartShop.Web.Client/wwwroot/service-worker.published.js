// Published service worker: caches the app shell for fast start and handles Web Push.
// API calls and media are never cached (always fresh data, private images).
self.importScripts('./service-worker-assets.js');
self.importScripts('./push-handlers.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'smartshop-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff2?$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/, /^app-config\.json$/];

async function onInstall() {
  const assetsRequests = self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
    // index.html is not integrity-checked: proxies such as Cloudflare (Rocket Loader, bot detection) rewrite HTML,
    // and one mismatch would fail the whole install, leaving the app without push notifications.
    .map(asset => new Request(asset.url, asset.url === 'index.html' ? { cache: 'no-cache' } : { integrity: asset.hash, cache: 'no-cache' }));
  await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
  self.skipWaiting();
}

async function onActivate() {
  const cacheKeys = await caches.keys();
  await Promise.all(cacheKeys.filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName).map(key => caches.delete(key)));
  await self.clients.claim();
}

async function onFetch(event) {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/') || url.pathname === '/app-config.json') {
    return fetch(event.request);
  }
  let cachedResponse = null;
  // Navigation requests are served the cached index.html (single page app).
  const shouldServeIndexHtml = event.request.mode === 'navigate';
  const request = shouldServeIndexHtml ? 'index.html' : event.request;
  const cache = await caches.open(cacheName);
  cachedResponse = await cache.match(request);
  return cachedResponse || fetch(event.request);
}
