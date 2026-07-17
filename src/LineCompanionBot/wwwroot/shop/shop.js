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
  statusEl.textContent = 'Choose an item:';
  const catalog = await fetch('/api/shop/catalog').then(r => r.json());
  for (const item of catalog) {
    const li = document.createElement('li');
    li.innerHTML = `<strong>${item.name}</strong><br>${item.description}
      <button data-product-id="${item.productId}" ${iapAvailable ? '' : 'disabled'}>Buy</button>`;
    const button = li.querySelector('button');
    button.addEventListener('click', () => buy(item, button));
    catalogEl.appendChild(li);
  }
  if (!iapAvailable) {
    statusEl.textContent = 'In-app purchase is not available in this client — Buy is disabled.';
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

main().catch(err => {
  statusEl.textContent = `Error: ${err.message}`;
});
