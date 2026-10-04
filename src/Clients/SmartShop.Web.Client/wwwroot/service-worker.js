// Development service worker: no offline caching (so code changes are picked up), only push notifications.
self.importScripts('./push-handlers.js');
self.addEventListener('fetch', () => { });
