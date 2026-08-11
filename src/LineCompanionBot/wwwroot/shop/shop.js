const statusEl = document.getElementById('status');
const catalogEl = document.getElementById('catalog');

async function main() {
  const config = await fetch('/api/shop/config').then(r => r.json());
  if (!config.liffId) {
    statusEl.textContent = 'Shop is not configured (LINE_MINIAPP_LIFF_ID is unset).';
    return;
  }

  await liff.init({ liffId: config.liffId });
  if (!liff.isLoggedIn()) {
    liff.login();
    return;
  }

  const iapAvailable = liff.isApiAvailable('iap');
  const devPurchaseEnabled = config.devPurchaseEnabled;
  statusEl.textContent = 'Choose an item:';
  const catalog = await fetch('/api/shop/catalog').then(r => r.json());
  for (const item of catalog) {
    const li = document.createElement('li');
    li.innerHTML = `<strong>${item.name}</strong><br>${item.description}
      <button data-product-id="${item.productId}" ${iapAvailable ? '' : 'disabled'}>Buy</button>`;
    const button = li.querySelector('button');
    button.addEventListener('click', () => buy(item, button));
    // Development-only shortcut (see /api/shop/dev/complete-purchase): grant the item without the real
    // IAP flow, so the downstream grant/notify/consume path can be tested before the MINI App's
    // in-app purchase review is approved. The server only exposes this in Development, so
    // config.devPurchaseEnabled is false — and this button absent — in a deployed app.
    if (devPurchaseEnabled) {
      const devButton = document.createElement('button');
      devButton.textContent = 'Mark purchased (dev)';
      devButton.addEventListener('click', () => devComplete(item, devButton));
      li.appendChild(devButton);
    }
    catalogEl.appendChild(li);
  }
  if (!iapAvailable) {
    statusEl.textContent = devPurchaseEnabled
      ? 'In-app purchase is unavailable here — use "Mark purchased (dev)" to test the downstream flow.'
      : 'In-app purchase is not available in this client — Buy is disabled.';
  }
}

async function buy(item, button) {
  if (!liff.isApiAvailable('iap')) {
    statusEl.textContent = 'In-app purchase is not available in this client.';
    return;
  }

  // Disabled for the whole reserve/consent/createPayment sequence: each of those creates or
  // spends a real LINE-side commitment (a reserved order, a consent prompt), and a second click
  // mid-flight would reserve a second order for the same item with no way to cancel the first.
  button.disabled = true;
  try {
    const profile = await liff.getProfile();
    const reserveResponse = await fetch('/api/shop/reserve', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        userId: profile.userId,
        productId: item.productId,
        liffAccessToken: liff.getAccessToken(),
        clientOs: liff.getOS(),
      }),
    });

    if (!reserveResponse.ok) {
      statusEl.textContent = `Failed to reserve ${item.name}.`;
      return;
    }

    const { orderId } = await reserveResponse.json();

    try {
      await liff.iap.requestConsentAgreement();
      await liff.iap.createPayment({ productId: item.productId, orderId });
      statusEl.textContent = `Purchase started for ${item.name} — you'll be notified in chat once it completes.`;
    } catch (err) {
      statusEl.textContent = `Purchase for ${item.name} was cancelled or failed: ${err.message ?? err}`;
    }
  } finally {
    button.disabled = false;
  }
}

// Development-only: bypass the real payment and ask the server to treat the item as purchased.
async function devComplete(item, button) {
  button.disabled = true;
  try {
    const profile = await liff.getProfile();
    const res = await fetch('/api/shop/dev/complete-purchase', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userId: profile.userId, productId: item.productId }),
    });
    if (!res.ok) {
      statusEl.textContent = `Dev grant failed for ${item.name} (HTTP ${res.status}).`;
      return;
    }
    const result = await res.json();
    statusEl.textContent = result.notified
      ? `Dev: granted ${item.name}. Check the chat for the notification, then tap Feed.`
      : `Dev: granted ${item.name} (no push — LINE_CHANNEL_ACCESS_TOKEN is unset). Tap Feed.`;
  } catch (err) {
    statusEl.textContent = `Dev grant error: ${err.message ?? err}`;
  } finally {
    button.disabled = false;
  }
}

main().catch(err => {
  statusEl.textContent = `Error: ${err.message}`;
});
