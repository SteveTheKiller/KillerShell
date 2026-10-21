// Read-only copy regression: hardware discovery is refreshed by reopening its tab.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const site = path.resolve(__dirname, '../../shell-landing');
const context = {};
context.window = context;
vm.createContext(context);
vm.runInContext(fs.readFileSync(path.join(site, 'ks-i18n.js'), 'utf8'), context);
const english = fs.readFileSync(path.join(site, 'help.html'), 'utf8').match(/data-i18n="hx_093">(.*?)<\/p>/)[1];
assert(english.includes('close and reopen the Performance tab'), 'English help must explain how to refresh hardware discovery.');
assert(!english.includes('New hardware appears on its own'));
const reopen = { sl: 'znova odprite', vi: 'mở lại', hu: 'nyissa meg újra', pl: 'otwórz ponownie',
  es: 'vuelve a abrir', de: 'erneut', fr: 'rouvrez', tr: 'yeniden açın', 'zh-cn': '重新打开',
  zh: '重新開啟', cs: 'znovu otevřete', ja: '開き直して', bn: 'আবার খুলুন', it: 'riapri',
  ru: 'снова откройте', kk: 'қайта ашыңыз', uk: 'знову відкрийте', nb: 'åpne', pt: 'reabra' };
assert.deepEqual(Object.keys(context.I18N).sort(), Object.keys(reopen).sort(), 'Every website locale must be checked.');
for (const [language, phrase] of Object.entries(reopen))
  assert(context.I18N[language].hx_093.includes(phrase), language + ': translated help must explain the reopening action.');
console.log('PASS hardware refresh instructions in English and all19 translated help entries.');
