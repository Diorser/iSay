import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const downloads = fs.readFileSync(path.join(root, 'website/dist/assets/downloads.js'), 'utf8');
const site = fs.readFileSync(path.join(root, 'website/dist/assets/site.js'), 'utf8');
const configuredRelease = JSON.parse(fs.readFileSync(path.join(root, 'website/dist/downloads/windows.json'), 'utf8'));
const originalHref = 'https://github.com/Diorser/iSay';
const valid = {
  schema: 1,
  roundTripHashVerified: true,
  status: 'available',
  architecture: 'win-x64',
  version: '0.10.1',
  bytes: 201508976,
  sha256: 'e1b82ebba0a65db79bbb9e7c4bff12383e6a1d35e3fe4338e176b9d025b8d61d',
  fileName: 'iSay-0.10.1-win-x64-full.exe',
  url: 'https://github.com/Diorser/iSay/releases/download/v0.10.1-installer-preview/iSay-0.10.1-win-x64-full.exe'
};
let count = 0;

// 最小 DOM 保留真实语言处理逻辑所需接口，不加载浏览器或发送网络请求。
function element(dataset = {}) {
  return {
    dataset: {...dataset}, textContent: '', children: [], attributes: {}, disabled: false,
    classList: {add() {}}, style: {setProperty() {}},
    setAttribute(name, value) { this.attributes[name] = value; },
    replaceChildren(...children) { this.children = children; this.textContent = children.map(child => child.textContent).join(''); },
    addEventListener() {}, append(...children) { this.children.push(...children); }
  };
}

async function run(release, options = {}) {
  const label = element({zh: '获取源码', en: 'Get the source'});
  const heading = element({zh: '源码预览', en: 'Source preview'});
  const paragraph = element({zh: '当前没有完整安装包', en: 'No complete installer yet'});
  const button = element();
  button.href = originalHref;
  button.querySelector = () => options.missing === 'label' ? null : label;
  const nodes = new Map([
    ['.hero-action a.button', button], ['.source-notice h3', heading], ['.source-notice p', paragraph],
    ['.language', element()], ['.waveform', element()],
    ['meta[name="description"]', element()], ['meta[property="og:title"]', element()], ['meta[property="og:description"]', element()]
  ]);
  const ids = new Map([['replay', element()], ['demo-output', element()]]);
  const listeners = new Map();
  const events = [];
  const fetches = [];
  const document = {
    baseURI: options.baseURI ?? 'https://isay-voice.pages.dev/', documentElement: element(),
    querySelector(selector) { return options.missing === selector ? null : nodes.get(selector) ?? null; },
    querySelectorAll(selector) { return selector === '[data-zh]' ? [label, heading, paragraph] : []; },
    getElementById(id) { return ids.get(id) ?? null; },
    createElement() { return element(); }, createTextNode(textContent) { return {textContent}; }
  };
  const window = {
    matchMedia() { return {matches: true}; }, clearTimeout() {}, setTimeout() {},
    localStorage: {getItem() { return 'zh'; }, setItem() {}}, location: {search: options.language === 'en' ? '?lang=en' : ''},
    addEventListener(type, handler) { listeners.set(type, handler); },
    dispatchEvent(event) { events.push(event.type); listeners.get(event.type)?.(event); }
  };
  const context = vm.createContext({
    window, document, URL, URLSearchParams,
    Event: class { constructor(type) { this.type = type; } },
    async fetch(url, init) {
      fetches.push({url: String(url), init});
      if (options.networkFailure) throw new Error('模拟网络中断');
      return {ok: options.httpOk !== false, async json() {
        if (options.jsonFailure) throw new Error('模拟损坏 JSON');
        return release;
      }};
    }
  });
  vm.runInContext(site, context, {filename: 'site.js'});
  vm.runInContext(downloads, context, {filename: 'downloads.js'});
  // 等待下载脚本中的两个异步步骤完成。
  await new Promise(resolve => setImmediate(resolve));
  return {button, label, heading, paragraph, events, fetches};
}

async function staysSource(name, release, options = {}) {
  const result = await run(release, options);
  assert.equal(result.button.href, originalHref, `${name}：保留源码地址`);
  assert.equal(result.label.dataset.zh, '获取源码', `${name}：保留源码文案`);
  assert.deepEqual(result.events, [], `${name}：不得发出安装包就绪事件`);
  count += 1;
}

if (configuredRelease.status === 'available') {
  const configured = await run(configuredRelease);
  assert.equal(configuredRelease.roundTripHashVerified, true, '已公开配置必须记录完整回下载核验');
  const expectedInstaller = `https://github.com/Diorser/iSay/releases/download/v${configuredRelease.version}-installer-preview/iSay-${configuredRelease.version}-win-x64-full.exe`;
  assert.equal(configured.button.href, expectedInstaller, '当前配置必须指向对应版本的公开安装器');
  count += 1;
} else {
  await staysSource('当前官网配置', configuredRelease);
}
for (const roundTripHashVerified of [false, null, undefined]) {
  await staysSource('未完成回下载核验', {...valid, roundTripHashVerified});
}
for (const status of ['unavailable', 'draft', 'unpublished', '', null]) {
  await staysSource(`未公开状态 ${status}`, {...valid, status});
}
for (const sha256 of ['', 'a'.repeat(63), 'g'.repeat(64), valid.sha256.toUpperCase(), null]) {
  await staysSource('无效 SHA256', {...valid, sha256});
}
for (const bytes of [0, -1, 1.5, 2147483649, '201508976', null]) {
  await staysSource('无效文件大小', {...valid, bytes});
}
for (const url of [
  valid.url.replace('https:', 'http:'),
  valid.url.replace('github.com', 'github.com.example.com'),
  valid.url.replace('Diorser', 'OtherOwner'),
  valid.url.replace('/iSay/', '/other/'),
  valid.url.replace('/download/', '/tag/'),
  `${valid.url}?download=1`, `${valid.url}#file`,
  valid.url.replace('github.com', 'user:password@github.com'),
  valid.url.replace('github.com', 'github.com:8443'),
  valid.url.replace('/v0.10.1-installer-preview/', '/'),
  valid.url.replace('/v0.10.1-installer-preview/', '/bad%20tag/'),
  'https://github.com/Diorser/iSay/archive/refs/heads/main.zip',
  'https://github.com/Diorser/iSay/releases/tag/untagged-07117e73e24545a5ac2e',
  'invalid'
]) await staysSource('无效下载地址', {...valid, url});
await staysSource('源码 ZIP 伪装安装包', {...valid, fileName: 'iSay-0.10.1.zip', url: valid.url.replace('.exe', '.zip')});
await staysSource('架构不匹配', {...valid, architecture: 'win-arm64'});
await staysSource('版本格式不匹配', {...valid, version: '0.10.1-preview'});
await staysSource('协议版本不匹配', {...valid, schema: 2});
await staysSource('空 JSON', null);
await staysSource('HTTP 失败', valid, {httpOk: false});
await staysSource('网络异常', valid, {networkFailure: true});
await staysSource('JSON 异常', valid, {jsonFailure: true});
for (const missing of ['.hero-action a.button', 'label', '.source-notice h3', '.source-notice p']) {
  await staysSource(`页面缺少 ${missing}`, valid, {missing});
}

for (const language of ['zh', 'en']) {
  const result = await run(valid, {language, baseURI: `https://isay-voice.pages.dev/${language === 'en' ? 'en/' : ''}`});
  assert.equal(result.button.href, valid.url, '只指向完整 EXE 的公开发行资产地址');
  assert.deepEqual(result.events, ['isay:release-ready'], '元数据验证后只发出一次就绪事件');
  assert.equal(result.label.dataset.zh, '免费下载 Windows 版');
  assert.equal(result.label.dataset.en, 'Download for Windows');
  assert.equal(result.label.textContent, result.label.dataset[language], '真实语言处理器应用下载文案');
  assert.equal(result.heading.textContent, result.heading.dataset[language]);
  assert.equal(result.paragraph.textContent, result.paragraph.dataset[language]);
  assert.match(result.paragraph.textContent, /0\.10\.1/);
  assert.match(result.paragraph.textContent, /192\.17 MiB/);
  assert.equal(result.button.title, `SHA256: ${valid.sha256}`);
  assert.equal(result.button.attributes['aria-label'], 'iSay 0.10.1 · 192.17 MiB');
  assert.equal(result.fetches.length, 1);
  assert.equal(result.fetches[0].url, 'https://isay-voice.pages.dev/downloads/windows.json');
  assert.equal(result.fetches[0].init.cache, 'no-store');
  count += 1;
}

console.log(`官网下载入口回归通过：${count} 项。`);
