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

  statusEl.textContent = 'Choose an item:';
  const catalog = await fetch('/api/shop/catalog').then(r => r.json());
  for (const item of catalog) {
    const li = document.createElement('li');
    li.innerHTML = `<strong>${item.name}</strong><br>${item.description}
      <button data-product-id="${item.productId}">Buy</button>`;
    li.querySelector('button').addEventListener('click', () => buy(item));
    catalogEl.appendChild(li);
  }
}

async function buy(item) {
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

  // TODO: hand `orderId` to LINE's in-app purchase SDK to actually drive the platform purchase
  // UI and complete the transaction. The exact call is outside Line.OpenApi.*'s scope (this repo
  // only wraps the server-side reserve API) — verify the current method name against LINE's
  // official MINI App IAP docs before wiring this up for real. Do not guess it.
  statusEl.textContent = `Reserved order ${orderId} for ${item.name} — `
    + `hand this to the platform IAP SDK to complete the purchase (see TODO in shop.js).`;
}

main().catch(err => {
  statusEl.textContent = `Error: ${err.message}`;
});
