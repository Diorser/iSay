/* 所有交互都在浏览器本地运行，不访问麦克风或发送分析请求。 */
(() => {
  'use strict';
  document.documentElement.classList.add('js');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const languageButton = document.querySelector('.language');
  const replayButton = document.getElementById('replay');
  const output = document.getElementById('demo-output');
  let language = 'zh';
  let timer;
  let generation = 0;

  const sentences = {zh: '今天，把灵感写下来。', en: 'Today, give your ideas a voice.'};
  const titles = {zh: 'iSay — 让想法，顺着声音变成文字', en: 'iSay — Your thoughts. In your own words.'};
  const descriptions = {zh: 'iSay，面向 Windows 的开源语音输入客户端。快捷键启停、实时转写、AI 整理与本地历史。当前提供 0.10.1 源码预览。', en: 'iSay is an open-source voice input client for Windows. Hotkey controls, live transcription, AI refinement and local history. Source preview 0.10.1.'};

  function setLanguage(next) {
    language = next === 'en' ? 'en' : 'zh';
    generation += 1;
    window.clearTimeout(timer);
    replayButton.disabled = false;
    document.documentElement.lang = language === 'zh' ? 'zh-CN' : 'en';
    document.querySelectorAll('[data-zh]').forEach(element => {
      const value = element.dataset[language];
      element.replaceChildren(...value.split('\\n').flatMap((line, index) => index ? [document.createElement('br'), document.createTextNode(line)] : [document.createTextNode(line)]));
    });
    document.querySelectorAll('[data-label-zh]').forEach(element => element.setAttribute('aria-label', element.dataset[language === 'zh' ? 'labelZh' : 'labelEn']));
    languageButton.textContent = language === 'zh' ? 'EN' : '中文';
    languageButton.setAttribute('aria-label', language === 'zh' ? 'Switch to English' : '切换到中文');
    document.title = titles[language];
    document.querySelector('meta[name="description"]').content = descriptions[language];
    document.querySelector('meta[property="og:title"]').content = titles[language];
    document.querySelector('meta[property="og:description"]').content = descriptions[language];
    output.textContent = sentences[language];
    try { window.localStorage.setItem('isay-language', language); } catch { /* 隐私模式禁止存储时保持当前页面语言。 */ }
  }

  languageButton.addEventListener('click', () => setLanguage(language === 'zh' ? 'en' : 'zh'));
  let initialLanguage = 'zh';
  try { initialLanguage = window.localStorage.getItem('isay-language') || 'zh'; } catch { /* 无法读取存储时默认显示中文。 */ }
  if (new URLSearchParams(window.location.search).get('lang') === 'en') initialLanguage = 'en';
  setLanguage(initialLanguage);

  const heights = [9,15,24,18,33,45,29,52,70,40,55,30,60,82,65,92,48,70,40,60,88,58,38,72,95,60,44,75,53,91,65,41,62,35,78,48,60,36,48,27,37,20,26,14,9];
  const waveform = document.querySelector('.waveform');
  heights.forEach((height, index) => {
    const bar = document.createElement('i');
    bar.style.setProperty('--height', height + '%');
    bar.style.setProperty('--delay', String(index * -65));
    bar.style.setProperty('--alpha', String(0.3 + (height / 140)));
    waveform.append(bar);
  });

  replayButton.addEventListener('click', () => {
    const text = sentences[language];
    if (reducedMotion.matches) { output.textContent = text; return; }
    const current = ++generation;
    const characters = Array.from(text);
    let index = 0;
    output.textContent = '';
    replayButton.disabled = true;
    const type = () => {
      if (generation !== current) return;
      output.textContent += characters[index++];
      if (index < characters.length) timer = window.setTimeout(type, language === 'zh' ? 130 : 65);
      else replayButton.disabled = false;
    };
    timer = window.setTimeout(type, 200);
  });

  if ('IntersectionObserver' in window && !reducedMotion.matches) {
    const observer = new IntersectionObserver(entries => entries.forEach(entry => {
      if (entry.isIntersecting) { entry.target.classList.add('visible'); observer.unobserve(entry.target); }
    }), {threshold: 0.1});
    document.querySelectorAll('.reveal').forEach(element => observer.observe(element));
  } else document.querySelectorAll('.reveal').forEach(element => element.classList.add('visible'));
})();
