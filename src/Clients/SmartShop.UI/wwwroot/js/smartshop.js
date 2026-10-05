// SmartShop browser helpers (shared by the web app and the mobile BlazorWebView).
window.smartshop = (() => {
  // ---------- storage ----------
  const storage = {
    get: (k) => { try { return localStorage.getItem(k); } catch { return null; } },
    set: (k, v) => { try { v === null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch { } },
  };

  // ---------- new-order alarm (Web Audio, no sound files needed) ----------
  let ctx = null, timer = null;
  const tone = (freq, start, duration) => {
    const osc = ctx.createOscillator(), gain = ctx.createGain();
    osc.type = 'square'; osc.frequency.value = freq;
    gain.gain.setValueAtTime(0.0001, ctx.currentTime + start);
    gain.gain.exponentialRampToValueAtTime(0.25, ctx.currentTime + start + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + start + duration);
    osc.connect(gain).connect(ctx.destination);
    osc.start(ctx.currentTime + start); osc.stop(ctx.currentTime + start + duration + 0.05);
  };
  const beep = () => {
    try {
      ctx = ctx || new (window.AudioContext || window.webkitAudioContext)();
      if (ctx.state === 'suspended') ctx.resume();
      tone(880, 0, 0.18); tone(1175, 0.22, 0.18); tone(1568, 0.44, 0.28);
      if (navigator.vibrate) navigator.vibrate([200, 100, 200]);
    } catch { }
  };
  const alarm = {
    beep,
    start: () => { if (timer) return; beep(); timer = setInterval(beep, 2500); },
    stop: () => { if (timer) { clearInterval(timer); timer = null; } },
  };
  // Browsers only allow audio after a user gesture: unlock on first tap.
  document.addEventListener('pointerdown', () => {
    try { ctx = ctx || new (window.AudioContext || window.webkitAudioContext)(); ctx.resume(); } catch { }
  }, { once: true });

  // ---------- LINE: LIFF inside the LINE app, OAuth + PKCE elsewhere ----------
  const loadScript = (src) => new Promise((resolve, reject) => {
    if (document.querySelector(`script[src="${src}"]`)) return resolve();
    const s = document.createElement('script'); s.src = src; s.onload = resolve; s.onerror = reject; document.head.appendChild(s);
  });
  const isLineBrowser = () => /Line\//i.test(navigator.userAgent);
  const liff = {
    isLineBrowser,
    init: async (liffId) => {
      if (!liffId) return { ready: false };
      await loadScript('https://static.line-scdn.net/liff/edge/2/sdk.js');
      await window.liff.init({ liffId });
      return { ready: true, inClient: window.liff.isInClient(), loggedIn: window.liff.isLoggedIn() };
    },
    idToken: () => window.liff?.getIDToken() ?? null,
    login: (redirectUri) => window.liff.login({ redirectUri }),
  };
  // Web login is a confidential client (the server exchanges the code with the channel secret), so no PKCE:
  // on phones LINE often returns to a new tab or another browser, where a per-tab verifier would be missing
  // ("login link expired" loop). The state is kept in localStorage so the same browser can still verify it.
  const lineLogin = (channelId, redirectUri, state) => {
    try { localStorage.setItem('smartshop.loginState', state); } catch { }
    const url = new URL('https://access.line.me/oauth2/v2.1/authorize');
    url.search = new URLSearchParams({
      response_type: 'code', client_id: channelId, redirect_uri: redirectUri, state, scope: 'openid profile', bot_prompt: 'aggressive',
    }).toString();
    window.location.href = url.toString();
  };
  const takeLoginState = () => {
    try { const s = localStorage.getItem('smartshop.loginState'); localStorage.removeItem('smartshop.loginState'); return s; } catch { return null; }
  };

  // ---------- Web Push ----------
  const urlB64ToUint8Array = (base64) => {
    const padding = '='.repeat((4 - base64.length % 4) % 4);
    const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'));
    return Uint8Array.from([...raw].map(c => c.charCodeAt(0)));
  };
  const push = {
    supported: () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window,
    permission: () => ('Notification' in window) ? Notification.permission : 'unsupported',
    subscribe: async (vapidPublicKey) => {
      const permission = await Notification.requestPermission();
      if (permission !== 'granted') return null;
      const registration = await navigator.serviceWorker.ready;
      const subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlB64ToUint8Array(vapidPublicKey) });
      const json = subscription.toJSON();
      return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth };
    },
  };

  // ---------- misc ----------
  const copy = async (text) => { try { await navigator.clipboard.writeText(text); return true; } catch { return false; } };
  const share = async (title, text, url) => {
    if (navigator.share) { try { await navigator.share({ title, text, url }); return true; } catch { return false; } }
    return copy(url);
  };
  const download = (name, base64, type) => {
    const a = document.createElement('a'); a.href = `data:${type};base64,${base64}`; a.download = name; a.click();
  };
  const scrollTo = (id) => document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  const setBadge = (count) => { try { count > 0 ? navigator.setAppBadge?.(count) : navigator.clearAppBadge?.(); } catch { } };
  const deviceLabel = () => {
    const ua = navigator.userAgent;
    const os = /iPhone|iPad/.test(ua) ? 'iOS' : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /Mac/.test(ua) ? 'Mac' : 'Device';
    const browser = /Line\//.test(ua) ? 'LINE' : /Edg\//.test(ua) ? 'Edge' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : /Firefox\//.test(ua) ? 'Firefox' : 'Browser';
    return `${browser} · ${os}`;
  };

  return { storage, alarm, liff, lineLogin, takeLoginState, push, copy, share, download, scrollTo, setBadge, deviceLabel };
})();
