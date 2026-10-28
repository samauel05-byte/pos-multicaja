const { chromium } = require('playwright');
(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1366, height: 800 } });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push('pageerror: ' + e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  const shot = n => page.screenshot({ path: `${process.env.POS_SHOTS || '.'}/${n}.png` });
  const B = process.env.POS_URL || 'http://127.0.0.1:5080';

  await page.goto(B);
  await page.fill('input[autocomplete=username]', 'cajero1');
  await page.fill('input[type=password]', 'Demo1234!');
  await page.click('button.primary');
  await page.waitForSelector('.sidebar');
  await page.selectOption('.side-foot select', { label: 'Caja 1' });
  await shot('01-sin-turno');

  // Abrir caja
  await page.click('text=Caja y turno');
  const hasOpen = await page.locator('text=Abrir caja').first().isVisible();
  if (hasOpen) {
    await page.click('button.primary:has-text("Abrir caja")');
    await page.fill('.modal input', '2000');
    await page.click('.modal-foot button.primary');
    await page.waitForSelector('text=Efectivo esperado en gaveta');
  }
  await shot('02-caja-abierta');

  // Vender: escanear código + cantidad
  await page.click('nav >> text=Venta');
  await page.waitForSelector('.tile');
  await page.fill('.searchbar input', '7501000000011');
  await page.keyboard.press('Enter');
  await page.fill('.searchbar input', '2*7501000000028');
  await page.keyboard.press('Enter');
  await page.waitForSelector('.cline >> nth=1');
  await page.waitForFunction(() => document.querySelector('.grand')?.textContent?.includes('125.00'));
  await shot('03-carrito');

  await page.keyboard.press('F9');
  await page.waitForSelector('.modal');
  await page.fill('.modal input >> nth=0', '200');
  await shot('04-cobro');
  await page.keyboard.press('Enter');
  await page.waitForSelector('text=Venta registrada');
  await page.waitForTimeout(800);
  await shot('05-venta-registrada');
  const num = await page.locator('.done h2').innerText();
  console.log('Venta registrada:', num, '| cambio:', await page.locator('.change').innerText());
  await page.keyboard.press('Enter');

  // Servidor caído: bloquea cobros
  await page.fill('.searchbar input', '7501000000035'); await page.keyboard.press('Enter');
  await page.waitForSelector('.cline');
  await ctx.route('**/api/health', r => r.abort());
  await page.waitForSelector('.downbar', { timeout: 9000 });
  const disabled = await page.locator('button.huge').isDisabled();
  console.log('Servidor caído -> botón cobrar deshabilitado:', disabled, '|', (await page.locator('button.huge').innerText()));
  await shot('06-servidor-caido');
  await ctx.unroute('**/api/health');
  await page.waitForSelector('.downbar', { state: 'detached', timeout: 9000 });

  // Cierre de caja
  await page.keyboard.press('F10'); page.once('dialog', d => d.accept());
  await page.click('nav >> text=Caja y turno');
  await page.waitForSelector('text=Efectivo esperado en gaveta');
  await page.click('button:has-text("Arqueo y cierre")');
  await page.waitForSelector('.denom');
  await shot('07-cierre');
  console.log('errores de consola:', JSON.stringify(errors));
  await browser.close();
})().catch(e => { console.error('E2E FALLÓ', e); process.exit(1); });
