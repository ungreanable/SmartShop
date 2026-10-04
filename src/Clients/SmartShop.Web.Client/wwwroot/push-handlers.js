// Web Push handlers shared by the development and published service workers.
self.addEventListener('push', (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { data = { title: 'SmartShop', body: event.data?.text() }; }
  const title = data.title || 'SmartShop';
  event.waitUntil(self.registration.showNotification(title, {
    body: data.body || '',
    icon: 'icons/icon-192.png',
    badge: 'icons/icon-192.png',
    tag: data.tag || undefined,
    renotify: !!data.urgent,
    requireInteraction: !!data.urgent,   // new orders stay on screen until handled
    vibrate: data.urgent ? [300, 100, 300, 100, 300] : [100],
    data: { url: data.url || '/' },
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = new URL(event.notification.data?.url || '/', self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of windows) {
      if ('focus' in client) { await client.focus(); if ('navigate' in client) await client.navigate(url); return; }
    }
    await clients.openWindow(url);
  })());
});
