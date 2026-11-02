/* 核验元数据后同步下载信息；异常时保留生成页面中已公开的静态入口。 */
(() => {
  'use strict';
  const repository = 'Diorser/iSay';
  async function load() {
    const response = await fetch(new URL('/downloads/windows.json', document.baseURI), {cache: 'no-store'});
    if (!response.ok) return;
    const release = await response.json();
    if (release.schema !== 1 || release.status !== 'available' || release.roundTripHashVerified !== true || release.architecture !== 'win-x64' ||
        !/^\d+\.\d+\.\d+$/.test(release.version) || !/^[a-f0-9]{64}$/.test(release.sha256) ||
        !Number.isSafeInteger(release.bytes) || release.bytes <= 0 || release.bytes > 2147483648 ||
        release.fileName !== `iSay-${release.version}-win-x64-full.exe`) return;
    const address = new URL(release.url);
    const segments = address.pathname.split('/');
    if (address.protocol !== 'https:' || address.hostname !== 'github.com' || address.port || address.username || address.password ||
        address.search || address.hash || segments.length !== 7 ||
        segments.slice(1, 5).join('/') !== `${repository}/releases/download` ||
        !/^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$/.test(segments[5]) || segments[6] !== release.fileName) return;
    const button = document.querySelector('.hero-action a.button');
    const label = button?.querySelector('span[data-zh]');
    const heading = document.querySelector('.source-notice h3');
    const paragraph = document.querySelector('.source-notice p');
    // 先确认所有目标存在，避免页面结构变化时只切换部分下载文案。
    if (!button || !label || !heading || !paragraph) return;
    label.dataset.zh = '免费下载 Windows 版';
    label.dataset.en = 'Download for Windows';
    button.href = address.href;
    button.setAttribute('aria-label', `iSay ${release.version} · ${(release.bytes / 1048576).toFixed(2)} MiB`);
    button.title = `SHA256: ${release.sha256}`;
    heading.dataset.zh = 'Windows 完整安装预览版';
    heading.dataset.en = 'Complete Windows installer preview';
    paragraph.dataset.zh = `版本 ${release.version}，${(release.bytes / 1048576).toFixed(2)} MiB。完整安装包已包含现有功能组件和必需环境，跟随向导即可安装。当前为安装预览版，建议先在自己的麦克风与常用软件中试用。`;
    paragraph.dataset.en = `Version ${release.version}, ${(release.bytes / 1048576).toFixed(2)} MiB. Includes current components and prerequisites. Follow the setup wizard, then try this preview with your microphone and everyday apps.`;
    document.querySelectorAll('[data-download]').forEach(link => {
      link.href = address.href;
      link.setAttribute('aria-label', `iSay ${release.version} · ${(release.bytes / 1048576).toFixed(2)} MiB`);
      link.title = `SHA256: ${release.sha256}`;
    });
    const edition = document.querySelector('[data-release-edition]');
    const version = document.querySelector('[data-release-version]');
    if (edition) {
      edition.dataset.zh = `Windows · 安装预览 ${release.version}`;
      edition.dataset.en = `Windows · Installer preview ${release.version}`;
    }
    if (version) {
      version.dataset.zh = `${release.version} 安装预览`;
      version.dataset.en = `${release.version} installer preview`;
    }
    window.iSayReleaseVersion = release.version;
    window.dispatchEvent(new Event('isay:release-ready'));
  }
  load().catch(() => { /* 元数据不可用时保留生成页面中已公开的静态下载入口。 */ });
})();
