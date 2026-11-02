/* 正文已在中英文静态页中生成；脚本只增强动效与已核验的下载信息。 */
(() => {
  'use strict';
  const queryEnglish = new URLSearchParams(window.location.search).get('lang') === 'en';
  if (queryEnglish && document.documentElement.lang !== 'en' && typeof window.location.replace === 'function') {
    window.location.replace('/en/');
    return;
  }
  const language = document.documentElement.lang === 'en' || queryEnglish ? 'en' : 'zh';
  function refreshText() {
    document.querySelectorAll('[data-zh]').forEach(element => {
      const value = element.dataset[language];
      if (typeof value !== 'string') return;
      element.replaceChildren(...value.split('\\n').flatMap((line, index) => index ? [document.createElement('br'), document.createTextNode(line)] : [document.createTextNode(line)]));
    });
  }
  window.addEventListener('isay:release-ready', refreshText);
  refreshText();
  // 先建立观察器再隐藏增强区块，能力缺失或异常时仍能阅读全部正文。
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  if ('IntersectionObserver' in window && !reducedMotion.matches) {
    try {
      const observer = new IntersectionObserver(entries => entries.forEach(entry => {
        if (entry.isIntersecting) { entry.target.classList.add('visible'); observer.unobserve(entry.target); }
      }), {threshold: 0.08});
      document.querySelectorAll('.reveal').forEach(element => observer.observe(element));
      document.documentElement.classList.add('js');
    } catch { /* 动效不可用时保留静态正文。 */ }
  }
})();
