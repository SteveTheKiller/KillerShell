// Read-only website check. Requires Playwright and an installed Edge or Chrome.
// Optional second argument saves screenshots under an existing evidence directory.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');

const site = path.resolve(__dirname, '../../shell-landing');
const channel = process.argv[2] || 'msedge';
const evidence = process.argv[3];
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css',
  '.png': 'image/png', '.svg': 'image/svg+xml', '.ttf': 'font/ttf' };
const server = http.createServer((request, response) => {
  const name = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
  const file = path.resolve(site, '.' + (name === '/' ? '/index.html' : name));
  if (!file.startsWith(site + path.sep)) { response.writeHead(403).end(); return; }
  fs.readFile(file, (error, bytes) => {
    if (error) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
    response.end(bytes);
  });
});

(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = 'http://127.0.0.1:' + server.address().port;
  let browser;
  try {
    browser = await chromium.launch({ channel, headless: true });
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    await context.route('**/*', route => route.request().url().startsWith(origin)
      ? route.continue() : route.abort());
    const page = await context.newPage();
    page.setDefaultTimeout(10000);
    await page.goto(origin);
    const languages = await page.locator('#langMenu [data-lang]').evaluateAll(nodes => nodes.map(n => n.dataset.lang));
    assert(languages.includes('en') && languages.length >= 19);
    for (const language of languages) {
      await page.evaluate(language => localStorage.setItem('kshell-lang', language), language);
      await page.reload();
      const trigger = page.locator('.sb-thumb').first();
      await trigger.waitFor();
      await trigger.focus();
      await page.keyboard.press('Enter');
      await page.locator('#lightbox.show').waitFor();
      for (const key of ['Tab', 'Shift+Tab', 'Tab']) {
        await page.keyboard.press(key);
        assert(await page.locator('#lightbox').evaluate(element => element.contains(document.activeElement)),
          language + ': keyboard focus escaped the screenshot modal on ' + key);
      }
      const close = page.locator('#lightbox button');
      assert.equal(await close.count(), 1, 'The screenshot has one explicit close button.');
      const expectedClose = await page.evaluate(language =>
        language === 'en' ? 'Close' : I18N[language].site_ui_close, language);
      assert.equal(await close.getAttribute('aria-label'), expectedClose);
      assert((await page.locator('#lightbox').getAttribute('aria-label')).length > 0);
      if (language === 'en') {
        const client = await context.newCDPSession(page);
        const tree = await client.send('Accessibility.getFullAXTree');
        const exposed = tree.nodes.filter(node => !node.ignored);
        assert(exposed.some(node => node.role?.value === 'dialog' && node.name?.value === 'Screenshot'));
        assert(exposed.some(node => node.role?.value === 'button' && node.name?.value === 'Close'));
        assert(!exposed.some(node => node.role?.value === 'link'), 'Background links are excluded from the browser accessibility tree.');
        await client.detach();
      }
      await page.locator('.tb-home').evaluate(element => element.focus());
      assert(await page.locator('#lightbox').evaluate(element => element.contains(document.activeElement)),
        language + ': background accepted focus while modal was open');
      await page.keyboard.press('Escape');
      assert(await trigger.evaluate(element => element === document.activeElement), 'Escape restores the invoking screenshot.');
      assert.equal(await page.locator('body > [inert]:not(#lightbox)').count(), 0, 'Background is available after closing.');
      await page.keyboard.press('Enter');
      await close.click();
      assert(await trigger.evaluate(element => element === document.activeElement), 'Close restores the invoking screenshot.');
    }
    for (const width of [1280, 360]) {
      await page.setViewportSize({ width, height: 900 });
      await page.locator('.sb-thumb').first().click();
      const close = page.locator('#lightbox button');
      const bounds = await close.boundingBox();
      assert(bounds && bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= width
        && bounds.y + bounds.height <= 900, 'Close control remains inside the viewport.');
      assert.equal(await page.locator('#lightbox').evaluate(element => element.scrollWidth <= element.clientWidth), true);
      if (evidence) await page.screenshot({ path: path.join(evidence, 'screenshot-modal-' + channel + '-' + width + '.png') });
      await page.keyboard.press('Escape');
    }
    await page.setViewportSize({ width: 640, height: 450 });
    await page.locator('.sb-thumb').first().click();
    assert(await page.locator('#lightbox button').isVisible(), 'Close remains available at the effective viewport of 200% zoom.');
    await page.keyboard.press('Escape');
    // Existing inert state belongs to the page, and must survive a modal round trip.
    await page.locator('.egg-toast').evaluate(element => element.inert = true);
    await page.locator('.sb-thumb').first().click();
    await page.keyboard.press('Escape');
    assert(await page.locator('.egg-toast').evaluate(element => element.inert));
    const targets = await page.locator('a[href]').evaluateAll(links => [...new Set(links
      .map(link => link.href).filter(url => url.startsWith(location.origin) && /\.html(?:#.*)?$/.test(url)))]);
    for (const target of targets) {
      const response = await page.goto(target);
      assert.equal(response.status(), 200, 'Shared navigation target loads: ' + target);
      assert((await page.title()).includes('KillerShell'));
    }
    console.log(channel + ' ' + browser.version() + ': ' + languages.length + ' locales, keyboard containment, close, restoration, narrow layout passed.');
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
