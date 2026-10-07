// Web Push handlers shared by the development and published service workers.
const iconUrl = new URL('icons/icon-192.png', self.location).href;
// Small status-bar icon (Android): must be a white silhouette on transparent, a colour icon shows as a square.
const badgeUrl = new URL('icons/badge-96.png', self.location).href;
// While the app is open and focused it shows its own in-app message; only a new order also pops up from the system.
const alwaysShown = new Set(['order.new']);
const focusedCloseAfterMs = 10000;

self.addEventListener('push', (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { data = { title: 'SmartShop', body: event.data?.text() }; }
  event.waitUntil((async () => {
    const windows = await clients.matchAll({ type: 'window', includeUncontrolled: true });
    const focused = windows.some(c => c.focused);
    if (focused && !alwaysShown.has(data.tag)) return; // Chrome allows skipping the notification while a tab is focused

    const title = data.title || 'SmartShop';
    const options = {
      body: data.body || '',
      icon: iconUrl,
      badge: badgeUrl,
      tag: data.tag || undefined,
      renotify: !!data.tag && !!data.urgent,
      requireInteraction: !!data.urgent && !focused,   // new orders stay on screen until handled, unless the app is in front
      vibrate: data.urgent ? [300, 100, 300, 100, 300] : [100],
      data: { url: data.url || '/' },
    };
    await self.registration.showNotification(title, options);
    if (!focused) return;
    // The user is looking at the app: the system pop-up goes away by itself.
    await new Promise(r => setTimeout(r, focusedCloseAfterMs));
    const shown = await self.registration.getNotifications(options.tag ? { tag: options.tag } : undefined);
    shown.filter(n => n.title === title && n.body === options.body).forEach(n => n.close());
  })());
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
