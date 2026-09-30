/* Optional analytics. No GA4/Clarity requests before an affirmative choice. */
(function () {
  'use strict';
  if (!['findra-search.netlify.app'].includes(location.hostname)) return;
  var key = 'findra-analytics-consent-v1', choice = null, started = false;
  try { choice = localStorage.getItem(key); } catch (_) {}
  if (choice !== 'granted' && choice !== 'denied') choice = null;
  function script(src) { var s = document.createElement('script'); s.async = true; s.src = src; document.head.appendChild(s); }
  function start() {
    if (started) return;
    started = true;
    document.querySelectorAll('input,textarea,select,[contenteditable],form').forEach(function (el) { el.setAttribute('data-clarity-mask', 'true'); });
    var mask = new MutationObserver(function () { document.querySelectorAll('input,textarea,select,[contenteditable],form').forEach(function (el) { if (!el.hasAttribute('data-clarity-mask')) el.setAttribute('data-clarity-mask','true'); }); });
    mask.observe(document.body,{childList:true,subtree:true});
    window.dataLayer = window.dataLayer || [];
    window.gtag = function () { window.dataLayer.push(arguments); };
    gtag('consent', 'default', {analytics_storage:'granted', ad_storage:'denied', ad_user_data:'denied', ad_personalization:'denied'});
    gtag('js', new Date());
    var clean = new URL(location.origin + location.pathname);
    ['utm_source','utm_medium','utm_campaign','utm_content','utm_term'].forEach(function (name) { var value = new URLSearchParams(location.search).get(name); if (value) clean.searchParams.set(name, value); });
    gtag('config', 'G-909686JLLS', {allow_google_signals:false, allow_ad_personalization_signals:false, page_location:clean.href, page_referrer:document.referrer ? document.referrer.split('?')[0].split('#')[0] : ''});
    script('https://www.googletagmanager.com/gtag/js?id=G-909686JLLS');
    window.clarity = window.clarity || function () { (window.clarity.q = window.clarity.q || []).push(arguments); };
    clarity('consentv2', {analytics_Storage:'granted', ad_Storage:'denied'});
    script('https://www.clarity.ms/tag/yqly8fd2cl');
    var seen = new Set();
    window.addEventListener('scroll', function () {
      var total = document.documentElement.scrollHeight - innerHeight;
      if (total <= 0) return;
      var depth = Math.min(100, Math.round(scrollY / total * 100));
      [25,50,75,100].forEach(function (n) { if (depth >= n && !seen.has(n)) { seen.add(n); gtag('event','scroll_depth',{percent_scrolled:n}); } });
    }, {passive:true});
  }
  function remember(value) { try { localStorage.setItem(key,value); } catch (_) {} choice = value; }
  function clearCookies() {
    document.cookie.split(';').forEach(function (row) {
      var name = row.trim().split('=')[0];
      if (!/^(_ga|_gid|_gat|_clck|_clsk)/.test(name)) return;
      ['',location.hostname,'.findra-search.netlify.app'].forEach(function (domain) { document.cookie = name + '=; Max-Age=0; path=/; SameSite=Lax' + (domain ? '; domain='+domain : ''); });
    });
  }
  // Remove leftover first-party cookies when no grant is active (including an expired choice).
  if (choice !== 'granted') clearCookies();
  var css = document.createElement('link'); css.rel = 'stylesheet'; css.href = '/analytics.css'; document.head.appendChild(css);
  var box = document.createElement('section'); box.className = 'findra-consent'; box.dir = 'ltr'; box.setAttribute('aria-label','Website analytics preferences');
  box.innerHTML = '<strong>Website analytics are your choice</strong><p>With your permission, Google Analytics measures visits, sources and page use, and Microsoft Clarity records website interactions such as clicks and scrolling. These tools use cookies. Form fields and inputs are masked. This applies only to this website: the Findra desktop app has no analytics or telemetry.</p><div class="findra-consent-actions"><button type="button" data-choice="granted">Allow analytics and recordings</button><button type="button" data-choice="denied">Reject optional analytics</button><a href="/privacy/">Privacy policy</a></div>';
  box.hidden = !!choice; document.body.appendChild(box);
  box.addEventListener('click', function (event) {
    var button = event.target.closest('button'); if (!button) return;
    var value = button.dataset.choice;
    if (!value) return;
    remember(value); box.hidden = true;
    if (value === 'granted') start();
    else {
      window['ga-disable-G-909686JLLS'] = true;
      if (window.gtag) gtag('consent','update',{analytics_storage:'denied',ad_storage:'denied',ad_user_data:'denied',ad_personalization:'denied'});
      if (window.clarity) clarity('consentv2',{analytics_Storage:'denied',ad_Storage:'denied'});
      clearCookies(); if (started) location.reload();
    }
  });
  var settings = document.createElement('button'); settings.type='button'; settings.className='findra-consent-settings'; settings.textContent='Privacy preferences'; settings.addEventListener('click',function(){box.hidden=false;box.querySelector('button').focus();}); document.body.appendChild(settings);
  if (choice === 'granted') start();
})();

