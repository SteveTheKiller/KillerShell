/* KillerShell site - shared chrome behavior (theme, accent, language, easter egg).
   Page-specific behavior (screenshot strip, outline scroll-spy) stays inline per page. */
(function () {
  var root = document.documentElement;
  var chromeEnglish = {"site_ui_theme":"Theme","site_ui_choose_theme":"Choose theme","site_ui_accent":"Accent color","site_ui_close":"Close","site_ui_language":"Language","site_ui_copy":"Copy to clipboard","site_ui_copied":"Copied","site_ui_click":"Click me","site_ui_home":"Home","site_ui_shortcuts":"Shortcut view","site_ui_screenshot":"Screenshot","site_ui_red":"Red","site_ui_orange":"Orange","site_ui_yellow":"Yellow","site_ui_green":"Green","site_ui_teal":"Teal","site_ui_blue":"Blue","site_ui_purple":"Purple","site_ui_magenta":"Magenta","site_ui_expand":"Expand section","site_ui_collapse":"Collapse section","site_ui_family":"Part of","footer_src":"Source on GitHub"};
  chromeEnglish.site_ui_more = 'More';
  chromeEnglish.site_ui_store = 'Get KillerShell from the Microsoft Store';
  function chromeText(key) {
    var lang = root.getAttribute('lang') || 'en';
    if (lang === 'zh-Hant') lang = 'zh';
    if (lang === 'zh-Hans') lang = 'zh-cn';
    var dict = window.I18N && window.I18N[lang];
    return dict && dict[key] != null ? dict[key] : chromeEnglish[key] || (EN && EN[key]);
  }
  function localizeChrome() {
    var lang = root.getAttribute('lang') || 'en';
    if (lang === 'zh-Hant') lang = 'zh';
    if (lang === 'zh-Hans') lang = 'zh-cn';
    var themeDict = (window.I18N && window.I18N[lang]) || {};
    document.querySelectorAll('.swatch[data-theme]').forEach(function (swatch) {
      if (!swatch.hasAttribute('data-en-theme-title')) swatch.setAttribute('data-en-theme-title', swatch.title);
      var title = themeDict['theme_' + swatch.getAttribute('data-theme')] || swatch.getAttribute('data-en-theme-title');
      swatch.title = title;
      swatch.setAttribute('aria-label', title);
    });
    function label(selector, key, attribute) {
      document.querySelectorAll(selector).forEach(function (element) { element.setAttribute(attribute, chromeText(key)); });
    }
    label('.tb-store', 'site_ui_store', 'title');
    label('.theme-toggle', 'site_ui_theme', 'title');
    label('.theme-toggle', 'site_ui_choose_theme', 'aria-label');
    label('.tgrp', 'site_ui_theme', 'aria-label');
    label('#accentToggle', 'site_ui_accent', 'title');
    label('#accentToggle, #accentPop, .accent-bar', 'site_ui_accent', 'aria-label');
    label('.accent-bar .x', 'site_ui_close', 'aria-label');
    label('#langToggle', 'site_ui_language', 'title');
    label('#langToggle, .lang-switch', 'site_ui_language', 'aria-label');
    label('.tb-home', 'site_ui_home', 'title');
    label('#verEgg', 'site_ui_click', 'title');
    label('.ks-viewtoggle', 'site_ui_shortcuts', 'aria-label');
    label('#lightbox', 'site_ui_screenshot', 'aria-label');
    label('#lightboxClose', 'site_ui_close', 'aria-label');
    label('#lightboxClose', 'site_ui_close', 'title');
    ['red','orange','yellow','green','teal','blue','purple','magenta'].forEach(function (color) {
      label('.acc[data-accent="' + color + '"]', 'site_ui_' + color, 'title');
      label('.acc[data-accent="' + color + '"]', 'site_ui_' + color, 'aria-label');
    });
    var accentLabel = document.querySelector('.accent-bar .lbl');
    if (accentLabel) {
      var lang = root.getAttribute('lang');
      if (lang === 'zh-Hant') lang = 'zh';
      if (lang === 'zh-Hans') lang = 'zh-cn';
      accentLabel.textContent = window.I18N && window.I18N[lang] ? window.I18N[lang].accent_label : 'accent:';
    }
  }

  function localizeExtras() {
    document.querySelectorAll('.ol-chev').forEach(function (button) { button.setAttribute('aria-label', chromeText('site_ui_expand')); });
    // Legacy fact labels without explicit translation keys retain their English fallback.
    document.querySelectorAll('.hero-info .row .k:not([data-i18n])').forEach(function (label) {
      if (!label.hasAttribute('data-en-label')) label.setAttribute('data-en-label', label.textContent.trim());
      var english = label.getAttribute('data-en-label');
      var text = chromeText('ui_' + english);
      label.textContent = text != null ? text : english;
    });
    [['data-i18n-title', 'title'], ['data-i18n-aria', 'aria-label']].forEach(function (pair) {
      document.querySelectorAll('[' + pair[0] + ']').forEach(function (element) {
        var english = 'data-en-' + pair[1];
        if (!element.hasAttribute(english)) element.setAttribute(english, element.getAttribute(pair[1]) || '');
        var text = chromeText(element.getAttribute(pair[0]));
        element.setAttribute(pair[1], text != null ? String(text).replace(/&amp;/g, '&') : element.getAttribute(english));
      });
    });
    document.querySelectorAll('.statusbar .left').forEach(function (footer) {
      var sourceLink = footer.querySelector('a[href*="github.com/SteveTheKiller/KillerShell"]');
      if (sourceLink) sourceLink.textContent = chromeText('footer_src');
      [].slice.call(footer.childNodes).forEach(function (node) {
        if (node.nodeType !== 3) return;
        if (!node._familyEnglish && node.nodeValue.indexOf('Part of') >= 0) node._familyEnglish = node.nodeValue;
        if (node._familyEnglish) node.nodeValue = node._familyEnglish.replace('Part of', chromeText('site_ui_family'));
      });
    });
    document.querySelectorAll('.sb-thumb').forEach(function (button) {
      var image = button.querySelector('img');
      if (!image) return;
      if (!button.dataset.englishDescription) button.dataset.englishDescription = image.alt;
      var description = screenshotDescription(+button.dataset.idx, button.dataset.englishDescription);
      button.title = description; button.setAttribute('aria-label', description);
      image.alt = description; image.title = description;
    });
    var preview = document.getElementById('lightboxImg');
    if (preview && preview.dataset.englishDescription) {
      var match = preview.src.match(/(\d+)\.png(?:[?#].*)?$/);
      if (match) {
        var description = screenshotDescription(+match[1], preview.dataset.englishDescription);
        preview.alt = description; preview.title = description;
        var caption = document.getElementById('lightboxCaption');
        if (caption) caption.textContent = description;
      }
    }
    document.querySelectorAll('svg text[data-max-width]').forEach(function (text) {
      text.removeAttribute('textLength');
      if (text.getComputedTextLength && text.getComputedTextLength() > +text.dataset.maxWidth) {
        text.setAttribute('textLength', text.dataset.maxWidth);
        text.setAttribute('lengthAdjust', 'spacingAndGlyphs');
      }
    });
    var heading = document.querySelector('.page-hero .page-title, .help-hero .page-title, .hero .tagline');
    var intro = document.querySelector('.page-hero p[data-i18n], .hero p[data-i18n], .help-hero p[data-i18n]');
    if (heading) {
      var titleKey = document.querySelector('[data-i18n="t_hero_kicker"]') ? 'nav_tech' : document.querySelector('[data-i18n="h_hero_kicker"]') ? 'nav_howto' : document.querySelector('[data-i18n="about_kicker"]') ? 'nav_about' : null;
      document.title = 'KillerShell: ' + (titleKey ? chromeText(titleKey) : heading.textContent.trim());
      document.querySelectorAll('meta[property="og:title"], meta[name="twitter:title"]').forEach(function (meta) { meta.content = document.title; });
    }
    if (intro) document.querySelectorAll('meta[name="description"], meta[property="og:description"], meta[name="twitter:description"]').forEach(function (meta) { meta.content = intro.textContent.trim(); });
  }
  function screenshotDescription(index, english) {
    var lang = root.getAttribute('lang') || 'en';
    if (lang === 'zh-Hant') lang = 'zh';
    if (lang === 'zh-Hans') lang = 'zh-cn';
    var dict = window.I18N && window.I18N[lang];
    if (!dict) return english;
    var captions = [
      dict.f_panes_t + ': PowerShell, ' + dict.f_editor_t + ', ' + dict.f_browse_t,
      dict.f_panes_t + ': ' + dict.f_fname_t + ', ' + dict.f_fileops_t,
      dict.f_pipe_t + ', ' + dict.f_editor_t,
      dict.f_panes_t + ': ' + dict.f_procsvc_t + ', ' + dict.f_reg_t,
      dict.f_evt_t + ' (Ctrl+F12)',
      dict.f_panes_t + ': ' + dict.f_tabs_t + ', ' + dict.f_browse_t,
      dict.f_storage_t + ', ' + dict.f_browse_t,
      dict.theme_light + ': ' + dict.f_fname_t
    ];
    // Demo is a fixed command-line mode; feature and theme names use the site locale.
    var caption = captions[index - 1];
    return caption ? 'KillerShell --demo: ' + caption.replace(/&amp;/g, '&') : english;
  }
  window.ksLocalizeChrome = function () { localizeChrome(); localizeExtras(); };
  var THEMES = ['dark','light','hc','blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning'];
  var NEUTRAL = ['dark','light','hc'];
  var THEMED = ['blood','greed','cyanotic','ectoplasm','decay','malaise','sepulchre','delirium','mourning'];  // fixed-color wordmark art
  // Per-family palette copied from the app: [ Accent (bright: text/links/logo/outlines), SelectionBg (darker fill: solid buttons, selected tab edges) ].
  var ACCENTS = {
    dark:  { red:['#FF7C77','#5E1C1C'], orange:['#E8962C','#F29A28'], yellow:['#EAD900','#F2E500'], green:['#22BB56','#1C5E38'], teal:['#1FB8A8','#1C5E5C'], blue:['#50AEE8','#1C3B5E'], purple:['#C08FE6','#411C5E'], magenta:['#FF6CD1','#FF2BBD'] },
    light: { red:['#901A1A','#901A1A'], orange:['#E8962C','#E8962C'], yellow:['#F2C500','#F2C500'], green:['#18541D','#18541D'], teal:['#085250','#085250'], blue:['#134D73','#134D73'], purple:['#5A1690','#5A1690'], magenta:['#8F0061','#8F0061'] },
    hc:    { red:['#FF3131','#FF3131'], orange:['#FF910A','#FF910A'], yellow:['#FFEB00','#FFEB00'], green:['#00FF66','#00FF66'], teal:['#0AFFE7','#0AFFE7'], blue:['#298DFF','#298DFF'], purple:['#C34AFF','#C34AFF'], magenta:['#FF2BBD','#FF2BBD'] }
  };
  // SelectionBg (muted accent) for inactive tab/card edges; brightens to accent on hover. Mirrors KillerTools themes.ts.
  var SEL = {
    dark:  { red:'#5E1C1C', orange:'#5E3B16', yellow:'#2A2908', green:'#1C5E38', teal:'#1C5E5C', blue:'#1C3B5E', purple:'#411C5E', magenta:'#2A0D24' },
    light: { red:'#901A1A', orange:'#E8962C', yellow:'#F2C500', green:'#18541D', teal:'#085250', blue:'#134D73', purple:'#5A1690', magenta:'#8F0061' },
    hc:    { red:'#380000', orange:'#4E2900', yellow:'#2E2B00', green:'#003314', teal:'#003832', blue:'#0A2C50', purple:'#250038', magenta:'#2E0022' }
  };
  function famFor(t) { return t === 'light' ? 'light' : t === 'hc' ? 'hc' : 'dark'; }
  // Text on an accent fill uses white or near-black, whichever contrasts more with that fill.
  // Swatches and filled buttons use a deeper red than the dark red accent text, which stays light for contrast.
  var FILL = { '#FF7C77': '#EA524C' };
  function fillOf(hex) { return FILL[String(hex).toUpperCase()] || hex; }
  function readableOn(hex) {
    var lum = function (h) { var c = [1, 3, 5].map(function (i) { var v = parseInt(h.substr(i, 2), 16) / 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }); return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]; };
    var l = lum(hex), dark = lum('#1e1f22');
    return (1.05 / (l + 0.05)) >= ((l + 0.05) / (dark + 0.05)) ? '#ffffff' : '#1e1f22';
  }

  var swatches = [].slice.call(document.querySelectorAll('.swatch'));
  var accDots  = [].slice.call(document.querySelectorAll('.acc'));
  var accentSwitch = document.getElementById('accentSwitch');
  var accToggle = document.getElementById('accentToggle');
  var accPop = document.getElementById('accentPop');
  var curAccent = 'blue';

  function buildThemeFlyout() {
    var group = document.querySelector('.topbar .tgrp');
    if (!group || !group.parentNode) return;
    var toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'theme-toggle';
    toggle.title = chromeText('site_ui_theme');
    toggle.setAttribute('aria-label', chromeText('site_ui_choose_theme'));
    toggle.setAttribute('aria-haspopup', 'true');
    toggle.setAttribute('aria-expanded', 'false');
    var preview = document.createElement('span');
    preview.setAttribute('aria-hidden', 'true');
    toggle.appendChild(preview);
    group.parentNode.insertBefore(toggle, group);
    function closeFlyout(focusToggle) {
      group.classList.remove('open');
      toggle.setAttribute('aria-expanded', 'false');
      if (focusToggle) toggle.focus();
    }
    function syncPreview(name) {
      var active = group.querySelector('.swatch[data-theme="' + name + '"]') || group.querySelector('.swatch');
      if (active) preview.className = active.className;
      preview.removeAttribute('aria-pressed');
    }
    toggle.addEventListener('click', function (e) {
      e.stopPropagation();
      var opening = !group.classList.contains('open');
      group.classList.toggle('open', opening);
      toggle.setAttribute('aria-expanded', opening ? 'true' : 'false');
    });
    group.addEventListener('click', function (e) {
      var swatch = e.target.closest('.swatch[data-theme]');
      if (!swatch) return;
      syncPreview(swatch.getAttribute('data-theme'));
      closeFlyout(false);
    });
    document.addEventListener('click', function (e) {
      if (!group.contains(e.target) && !toggle.contains(e.target)) closeFlyout(false);
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && group.classList.contains('open')) closeFlyout(true);
    });
    syncPreview(root.getAttribute('data-theme') || 'dark');
  }
  buildThemeFlyout();

  function applyAccent(name) {
    var theme = root.getAttribute('data-theme');
    var fam = famFor(theme);
    if (!ACCENTS[fam][name]) name = 'blue';
    ['dark', 'light', 'hc'].forEach(function (neutralTheme) {
      var preview = ACCENTS[neutralTheme][name];
      if (preview) document.querySelectorAll('.sw-' + neutralTheme).forEach(function (dot) {
        dot.style.setProperty('--sw-accent', neutralTheme === 'light' && name === 'yellow' ? 'linear-gradient(#FFF5A3, #FFD43B)' : fillOf(preview[0]));
      });
    });
    curAccent = name;
    var pair = ACCENTS[fam][name];
    var neutral = NEUTRAL.indexOf(theme) >= 0;
    root.toggleAttribute('data-yellow-outline', neutral && fam === 'light' && (name === 'yellow' || name === 'orange'));
    if (neutral) {
      root.style.setProperty('--accent', pair[0]);
      root.style.setProperty('--btn', pair[1]);
      root.style.setProperty('--sel', (SEL[fam] && SEL[fam][name]) || pair[1]);
      root.style.setProperty('--btn-text', readableOn(pair[1]));
      root.style.setProperty('--accent-text', readableOn(pair[0]));
      try { localStorage.setItem('kshell-av', pair[0] + '|' + pair[1]); } catch (e) {}
    } else {
      root.style.removeProperty('--accent');
      root.style.removeProperty('--btn');
      root.style.removeProperty('--sel');
      root.style.removeProperty('--btn-text');
      root.style.removeProperty('--accent-text');
    }
    accDots.forEach(function (d) {
      var p = ACCENTS[fam][d.dataset.accent];
      if (p) { d.style.background = fillOf(p[0]); d.style.color = fillOf(p[0]); }
      d.setAttribute('aria-pressed', d.dataset.accent === name ? 'true' : 'false');
    });
    if (accToggle) { accToggle.style.background = fillOf(pair[0]); accToggle.title = chromeText('site_ui_accent'); }
    try { localStorage.setItem('kshell-accent', name); } catch (e) {}
    updateLogos();
  }
  function updateLogos() {
    var theme = root.getAttribute('data-theme');
    var src;
    if (THEMED.indexOf(theme) >= 0) {
      // Fixed-color themes carry their own wordmark art, colored with the same PrimaryBrush
      // used by the app's live title-bar wordmark (make-logo-svgs.py --themes).
      src = 'brand/killershell-logo-' + theme + '.svg';
    } else {
      var variant = theme === 'light' ? 'light' : theme === 'hc' ? 'black' : 'dark';
      var color = (NEUTRAL.indexOf(theme) >= 0) ? curAccent : 'blue';
      src = 'brand/killershell-logo-' + variant + '-' + color + '.svg';
    }
    var imgs = document.querySelectorAll('img.wm-logo');
    for (var i = 0; i < imgs.length; i++) imgs[i].src = src;
  }

  function setTheme(name) {
    if (THEMES.indexOf(name) < 0) name = 'dark';
    root.setAttribute('data-theme', name);
    try { localStorage.setItem('kshell-theme', name); } catch (e) {}
    swatches.forEach(function (s) { s.setAttribute('aria-pressed', s.dataset.theme === name ? 'true' : 'false'); });
    if (accentSwitch) accentSwitch.hidden = NEUTRAL.indexOf(name) < 0;
    applyAccent(curAccent);
  }

  swatches.forEach(function (s) { s.addEventListener('click', function () { setTheme(s.dataset.theme); if (NEUTRAL.indexOf(s.dataset.theme) >= 0) showAccentBar(); else hideAccentBar(); }); });
  // Build a drop-down accent bar under the toolbar (moves the swatches out of the small header popup).
  var accentBar = null;
  var topbarEl = document.querySelector('.topbar');
  if (topbarEl && accDots.length) {
    accentBar = document.createElement('div');
    accentBar.className = 'accent-bar';
    var pill = document.createElement('div'); pill.className = 'pill';
    var grip = document.createElement('span'); grip.className = 'grip'; grip.setAttribute('aria-hidden', 'true');
    pill.appendChild(grip);
    var blbl = document.createElement('span'); blbl.className = 'lbl'; blbl.textContent = 'accent:';
    pill.appendChild(blbl);
    accDots.forEach(function (d) { pill.appendChild(d); });
    var bx = document.createElement('button'); bx.className = 'x'; bx.setAttribute('aria-label', chromeText('site_ui_close')); bx.innerHTML = '&times;';
    bx.addEventListener('click', hideAccentBar);
    pill.appendChild(bx);
    accentBar.appendChild(pill);
    topbarEl.parentNode.insertBefore(accentBar, topbarEl.nextSibling);
    if (accPop) accPop.remove();

    // Drag the strip sideways by its grip, clamped so it stays inside the content pane (the frame).
    var dragDx = 0, dragging = false, dragStartX = 0, dragStartDx = 0;
    function dragClamp(v) {
      var vw = window.innerWidth, pw = pill.offsetWidth, pad = 6, left = 8, right = vw - 8;
      var f = document.querySelector('.content');
      // Extra inset on the right so the pill clears the content scrollbar at its max position.
      if (f) { var fr = f.getBoundingClientRect(); if (fr.width > 0) { left = fr.left + pad; right = fr.right - pad - 12; } }
      var centerLeft = vw / 2 - pw / 2, min = left - centerLeft, max = right - pw - centerLeft;
      if (min > max) return 0;
      return Math.max(min, Math.min(max, v));
    }
    grip.addEventListener('mousedown', function (e) {
      dragging = true; dragStartX = e.clientX; dragStartDx = dragDx;
      document.body.style.userSelect = 'none'; e.preventDefault();
    });
    window.addEventListener('mousemove', function (e) {
      if (!dragging) return;
      dragDx = dragClamp(dragStartDx + (e.clientX - dragStartX));
      pill.style.transform = 'translateX(' + dragDx + 'px)';
    });
    window.addEventListener('mouseup', function () {
      if (!dragging) return; dragging = false; document.body.style.userSelect = '';
    });
    function dockAccentBar() {
      var contentPane = document.querySelector('.content');
      if (contentPane) accentBar.style.top = Math.round(contentPane.getBoundingClientRect().top) + 'px';
    }
    window.addEventListener('resize', function () { dockAccentBar(); dragDx = dragClamp(dragDx); pill.style.transform = 'translateX(' + dragDx + 'px)'; });
    // Default position: top-right corner, nearest the theme picker (still draggable from there).
    requestAnimationFrame(function () { dragDx = dragClamp(1e6); pill.style.transform = 'translateX(' + dragDx + 'px)'; });
  }
  function showAccentBar() { if (accentBar && NEUTRAL.indexOf(root.getAttribute('data-theme')) >= 0) { var contentPane = document.querySelector('.content'); if (contentPane) accentBar.style.top = Math.round(contentPane.getBoundingClientRect().top) + 'px'; accentBar.classList.add('show'); if (accToggle) accToggle.setAttribute('aria-expanded', 'true'); } }
  function hideAccentBar() { if (accentBar) { accentBar.classList.remove('show'); if (accToggle) accToggle.setAttribute('aria-expanded', 'false'); } }
  accDots.forEach(function (d) { d.addEventListener('click', function () { applyAccent(d.dataset.accent); }); });
  if (accToggle) {
    accToggle.addEventListener('click', function (e) { e.stopPropagation(); if (accentBar && accentBar.classList.contains('show')) hideAccentBar(); else showAccentBar(); });
  }
  document.addEventListener('click', function (e) { if (accentBar && accentBar.classList.contains('show') && !e.target.closest('.accent-bar') && !e.target.closest('#accentToggle')) hideAccentBar(); });

  // ---- i18n (English in the HTML; all eleven translated dictionaries are complete) ----
  var I18N = (typeof window !== 'undefined' && window.I18N) ? window.I18N : {};
  var EN = {};
  document.querySelectorAll('[data-i18n]').forEach(function (n) { EN[n.getAttribute('data-i18n')] = n.innerHTML; });
  var LANGS = ['en','es','de','fr','it','tr','vi','zh','zh-cn','bn','hu','pl','cs','ja','ru','uk','nb','pt','kk','sl'];
  var FLAGS = {
    sl: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#fff"/><rect y="8" width="24" height="8" fill="#005da4"/><rect y="16" width="24" height="8" fill="#ed1c24"/><path d="M5 4h6v5c0 3-3 4-3 4S5 12 5 9Z" fill="#005da4" stroke="#ed1c24" stroke-width=".6"/><path d="m5.5 9 1.2-1.7L8 9l1.3-1.7 1.2 1.7v1H5.5Z" fill="#fff"/><g fill="#ffdd00"><circle cx="7" cy="5.3" r=".35"/><circle cx="9" cy="5.3" r=".35"/><circle cx="8" cy="6.3" r=".35"/></g></svg>',
    en: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><g fill="#b22234"><rect width="24" height="1.85"/><rect y="3.7" width="24" height="1.85"/><rect y="7.4" width="24" height="1.85"/><rect y="11.1" width="24" height="1.85"/><rect y="14.8" width="24" height="1.85"/><rect y="18.5" width="24" height="1.85"/><rect y="22.2" width="24" height="1.8"/></g><rect width="11" height="12.95" fill="#3c3b6e"/></svg>',
    cs: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#d7141a"/><polygon points="0,0 12,12 0,24" fill="#11457e"/></svg>',
    pl: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#fff"/><rect y="12" width="24" height="12" fill="#dc143c"/></svg>',
    es: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#c60b1e"/><rect y="6" width="24" height="12" fill="#ffc400"/></svg>',
    de: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#000"/><rect y="8" width="24" height="8" fill="#dd0000"/><rect y="16" width="24" height="8" fill="#ffce00"/></svg>',
    fr: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#0055a4"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ef4135"/></svg>',
    it: '<svg viewBox="0 0 24 24"><rect width="8" height="24" fill="#009246"/><rect x="8" width="8" height="24" fill="#fff"/><rect x="16" width="8" height="24" fill="#ce2b37"/></svg>',
    ja: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fff"/><circle cx="12" cy="12" r="7" fill="#bc002d"/></svg>',
    tr: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#e30a17"/><circle cx="9.5" cy="12" r="5" fill="#fff"/><circle cx="11" cy="12" r="4" fill="#e30a17"/><polygon points="15.5,9.4 16.12,11.15 17.97,11.2 16.5,12.32 17.03,14.1 15.5,13.05 13.97,14.1 14.5,12.32 13.03,11.2 14.88,11.15" fill="#fff"/></svg>',
    vi: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#da251d"/><polygon points="12,5 13.65,10.1 19,10.1 14.67,13.25 16.32,18.35 12,15.2 7.68,18.35 9.33,13.25 5,10.1 10.35,10.1" fill="#ff0"/></svg>',
    zh: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#fe0000"/><rect width="12" height="12" fill="#000095"/><polygon points="6,3 7.2,6.6 11,6.6 7.9,8.8 9.1,12.4 6,10.2 2.9,12.4 4.1,8.8 1,6.6 4.8,6.6" fill="#fff"/></svg>',
    'zh-cn': '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#de2910"/><polygon points="4,3 4.9,5.6 7.6,5.6 5.4,7.3 6.2,9.9 4,8.3 1.8,9.9 2.6,7.3 0.4,5.6 3.1,5.6" fill="#ffde00"/></svg>',
    bn: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#006a4e"/><circle cx="10.5" cy="12" r="6" fill="#f42a41"/></svg>',
    hu: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#ce2939"/><rect y="8" width="24" height="8" fill="#fff"/><rect y="16" width="24" height="8" fill="#477050"/></svg>',
    ru: '<svg viewBox="0 0 24 24"><rect width="24" height="8" fill="#fff"/><rect y="8" width="24" height="8" fill="#0039a6"/><rect y="16" width="24" height="8" fill="#d52b1e"/></svg>',
    uk: '<svg viewBox="0 0 24 24"><rect width="24" height="12" fill="#0057b7"/><rect y="12" width="24" height="12" fill="#ffd700"/></svg>',
    nb: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#ba0c2f"/><rect x="6" width="6" height="24" fill="#fff"/><rect y="9" width="24" height="6" fill="#fff"/><rect x="7.5" width="3" height="24" fill="#00205b"/><rect y="10.5" width="24" height="3" fill="#00205b"/></svg>',
    pt: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#009c3b"/><polygon points="12,3.5 21.5,12 12,20.5 2.5,12" fill="#ffdf00"/><circle cx="12" cy="12" r="4.6" fill="#002776"/></svg>',
    kk: '<svg viewBox="0 0 24 24"><rect width="24" height="24" fill="#00afca"/><circle cx="12" cy="10.5" r="4" fill="#fec50c"/><polygon points="12,15.5 15.5,18.5 12,17.6 8.5,18.5" fill="#fec50c"/></svg>'
  };
  var langItems = [].slice.call(document.querySelectorAll('.lang-item'));
  var langToggle = document.getElementById('langToggle');
  var langMenu = document.getElementById('langMenu');

  function applyLang(lang) {
    if (LANGS.indexOf(lang) < 0) lang = 'en';
    root.setAttribute('lang', lang === 'zh' ? 'zh-Hant' : (lang === 'zh-cn' ? 'zh-Hans' : lang));
    var dict = (lang === 'en') ? EN : (I18N[lang] || {});
    document.querySelectorAll('[data-i18n]').forEach(function (n) {
      var k = n.getAttribute('data-i18n');
      n.innerHTML = (dict && dict[k] != null) ? dict[k] : EN[k];
    });
    localizeChrome();
    localizeExtras();
    langItems.forEach(function (b) { b.setAttribute('aria-pressed', b.dataset.lang === lang ? 'true' : 'false'); });
    if (langToggle) langToggle.innerHTML = FLAGS[lang] || FLAGS.en;
    try { localStorage.setItem('kshell-lang', lang); } catch (e) {}
    try { window.dispatchEvent(new CustomEvent('ks-language-changed', { detail: { lang: lang } })); } catch (e) {}
  }
  function closeLangMenu() { if (langMenu) { langMenu.hidden = true; langToggle.setAttribute('aria-expanded', 'false'); } }
  if (langToggle && langMenu) {
    langToggle.addEventListener('click', function (e) {
      e.stopPropagation();
      var willOpen = langMenu.hidden;
      langMenu.hidden = !willOpen;
      langToggle.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
    });
    langItems.forEach(function (b) { b.addEventListener('click', function () { applyLang(b.dataset.lang); closeLangMenu(); }); });
    document.addEventListener('click', function (e) { if (!langMenu.hidden && !e.target.closest('.lang-switch')) closeLangMenu(); });
  }

  // ---- Easter egg: click the version number ----
  // A small rotating set instead of one static line (2026-08-03), so clicking it more than
  // once is still worth doing. Picked without immediately repeating the line just shown.
  var eggLines = [
    'Explorer, cmd and Notepad walk into one window. Only one walks out.',
    "Task Manager called. It wants its process list back.",
    'Four tools walked into a tab strip. KillerShell walked out wearing all of them.',
    'PowerShell, Registry Editor and a text editor, cornered in one exe. Nobody called for backup.',
    "It's called KillerShell because “FileManagerButAlsoATerminalAndAlsoAnEditor.exe” didn't fit on the icon.",
    "One exe, no installer, no subscription - the only thing dying here is your Explorer.exe habit."
  ];
  var lastEggLine = -1;
  function nextEggLine() {
    if (eggLines.length < 2) return eggLines[0];
    var i;
    do { i = Math.floor(Math.random() * eggLines.length); } while (i === lastEggLine);
    lastEggLine = i;
    return chromeText('site_egg_' + i) || eggLines[i];
  }

  var verEgg = document.getElementById('verEgg');
  var eggToast = document.getElementById('eggToast');
  if (verEgg) verEgg.addEventListener('click', function () {
    for (var i = 0; i < 18; i++) {
      var d = document.createElement('span');
      d.className = 'drip';
      d.style.left = (Math.random() * 100) + 'vw';
      d.style.height = (18 + Math.random() * 64) + 'px';
      d.style.opacity = (0.6 + Math.random() * 0.4).toFixed(2);
      var dur = 1.1 + Math.random() * 1.6;
      d.style.animation = 'dripfall ' + dur + 's linear forwards';
      d.style.animationDelay = (Math.random() * 0.5) + 's';
      document.body.appendChild(d);
      (function (el) { setTimeout(function () { el.remove(); }, (dur + 0.8) * 1000); })(d);
    }
    if (eggToast) {
      eggToast.textContent = nextEggLine();
      eggToast.classList.add('show');
      clearTimeout(verEgg._t);
      verEgg._t = setTimeout(function () { eggToast.classList.remove('show'); }, 2800);
    }
  });

  // ---- Init ----
  var savedTheme = 'dark', savedAccent = 'blue', savedLang = 'en';
  try {
    savedTheme  = localStorage.getItem('kshell-theme')  || savedTheme;
    savedAccent = localStorage.getItem('kshell-accent') || savedAccent;
    savedLang   = localStorage.getItem('kshell-lang')   || savedLang;
  } catch (e) {}
  curAccent = savedAccent;
  setTheme(savedTheme);
  applyLang(savedLang);
})();
/* Top bar fit: the bar never wraps. When it runs out of room, the Microsoft Store and KillerMCP
   buttons shrink to their logos first, then the navigation links move into a More menu from the
   end, then the Download button drops its label, and last the Store and KillerMCP buttons join
   the menu. Re-measured on resize, font load and language changes. */
(function () {
  var bar = document.querySelector('.topbar');
  if (!bar || !window.MutationObserver) return;
  var nav = bar.querySelector('.tb-nav');
  var more = document.createElement('div');
  more.className = 'tb-more';
  more.hidden = true;
  var btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'tb-more-toggle';
  btn.setAttribute('aria-haspopup', 'true');
  btn.setAttribute('aria-expanded', 'false');
  btn.setAttribute('aria-label', 'More');
  btn.setAttribute('data-i18n-aria', 'site_ui_more');
  btn.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>';
  var menu = document.createElement('div');
  menu.className = 'lang-menu tb-more-menu';
  menu.hidden = true;
  more.appendChild(btn);
  more.appendChild(menu);
  if (nav && nav.parentNode === bar) bar.insertBefore(more, nav.nextSibling); else bar.appendChild(more);
  if (window.ksLocalizeChrome) window.ksLocalizeChrome();

  function mark(el) {
    if (!el) return null;
    var m = document.createComment('tb-slot');
    el.parentNode.insertBefore(m, el.nextSibling);
    return { el: el, slot: m };
  }
  var links = nav ? [].slice.call(nav.children).map(mark) : [];
  var extras = [bar.querySelector('.tb-mcp-link'), bar.querySelector('.tb-store')].filter(function (e) { return e && e.parentNode === bar; }).map(mark);
  var all = links.concat(extras);
  var busy = false;

  function over() { return bar.scrollWidth > bar.clientWidth + 1; }
  function close() { menu.hidden = true; btn.setAttribute('aria-expanded', 'false'); }
  function fit() {
    busy = true;
    close();
    all.forEach(function (m) { m.slot.parentNode.insertBefore(m.el, m.slot); });
    bar.classList.remove('tb-compact', 'tb-dl-icon');
    more.hidden = true;
    if (over()) bar.classList.add('tb-compact');
    if (over()) {
      more.hidden = false;
      for (var i = links.length - 1; i >= 0 && over(); i--) menu.insertBefore(links[i].el, menu.firstChild);
      if (over()) bar.classList.add('tb-dl-icon');
      for (var j = 0; j < extras.length && over(); j++) menu.appendChild(extras[j].el);
      if (!menu.children.length) more.hidden = true;
    }
    observer.takeRecords();
    busy = false;
  }
  var queued = false;
  function queue() {
    if (queued) return;
    queued = true;
    requestAnimationFrame(function () { queued = false; fit(); });
  }
  var observer = new MutationObserver(function () { if (!busy) queue(); });
  observer.observe(bar, { subtree: true, childList: true, characterData: true });
  window.addEventListener('resize', queue);
  // Logos, flags and fonts change widths without a DOM change, so watch every item's size too.
  if (window.ResizeObserver) {
    var sizes = new ResizeObserver(function () { if (!busy) queue(); });
    sizes.observe(bar);
    [].forEach.call(bar.children, function (c) { sizes.observe(c); });
  }
  window.addEventListener('load', queue);
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(queue);
  btn.addEventListener('click', function (e) {
    e.stopPropagation();
    var open = menu.hidden;
    menu.hidden = !open;
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
  });
  document.addEventListener('click', function (e) { if (!menu.hidden && !more.contains(e.target)) close(); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !menu.hidden) { close(); btn.focus(); } });
  fit();
})();
/* Film Grain: the same switch as the app's theme menu and KillerTools, on the family Alt+Shift+G chord.
   On by default; off sets data-grain="off", which empties --grain on every grained surface. */
(function () {
  'use strict';
  var root = document.documentElement;
  var KEY = 'film-grain';
  var LABELS = {
    en: 'Film Grain', bn: 'ফিল্ম গ্রেইন', cs: 'Filmové zrno', de: 'Filmkorn', es: 'Grano de película',
    fr: 'Grain de film', hu: 'Filmszemcse', it: 'Grana pellicola', ja: 'フィルムグレイン',
    kk: 'Пленка түйіршігі', nb: 'Filmkorn', no: 'Filmkorn', pl: 'Ziarno filmowe', pt: 'Granulação de filme',
    ru: 'Зернистость плёнки', sl: 'Filmska zrnatost', tr: 'Film greni', uk: 'Зернистість плівки',
    vi: 'Hạt phim', 'zh-hant': '底片顆粒', 'zh-hans': '胶片颗粒'
  };
  var box = null, text = null;
  function apply(on) {
    root.setAttribute('data-grain', on ? 'on' : 'off');
    if (box) box.checked = on;
  }
  function set(on) {
    apply(on);
    try { localStorage.setItem(KEY, on ? 'on' : 'off'); } catch (e) {}
  }
  function label() {
    var lang = (root.getAttribute('lang') || 'en').toLowerCase();
    if (lang.indexOf('zh') === 0) lang = /hant|tw|hk/.test(lang) ? 'zh-hant' : 'zh-hans';
    else lang = lang.split('-')[0];
    return LABELS[lang] || LABELS.en;
  }
  function localize() {
    if (!text) return;
    text.textContent = label();
    text.parentNode.title = label() + ' (Alt+Shift+G)';
  }
  var saved = 'on';
  try { saved = localStorage.getItem(KEY) || 'on'; } catch (e) {}
  apply(saved !== 'off');

  var group = document.querySelector('.topbar .tgrp');
  if (group) {
    var sep = document.createElement('div');
    sep.className = 'tgrp-sep';
    sep.setAttribute('aria-hidden', 'true');
    var row = document.createElement('label');
    row.className = 'tgrp-grain';
    box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = saved !== 'off';
    box.addEventListener('change', function () { set(box.checked); });
    text = document.createElement('span');
    row.appendChild(box);
    row.appendChild(text);
    group.appendChild(sep);
    group.appendChild(row);
    localize();
    new MutationObserver(localize).observe(root, { attributes: true, attributeFilter: ['lang'] });
  }
  document.addEventListener('keydown', function (e) {
    if (e.altKey && e.shiftKey && !e.ctrlKey && !e.metaKey && e.code === 'KeyG') {
      e.preventDefault();
      if (!e.repeat) set(root.getAttribute('data-grain') === 'off');
    }
  });
})();
