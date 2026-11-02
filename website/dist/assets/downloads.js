/* 只有公开安装器的完整元数据通过校验后才提供下载，不链接草稿或源码压缩包。 */
(() => {
  'use strict';
  const repository = 'Diorser/iSay';
  async function load() {
    const response = await fetch(new URL('./downloads/windows.json', document.baseURI), {cache: 'no-store'});
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
    label.dataset.zh = '下载 Windows 安装包';
    label.dataset.en = 'Download for Windows';
    button.href = address.href;
    button.setAttribute('aria-label', `iSay ${release.version} · ${(release.bytes / 1048576).toFixed(2)} MiB`);
    button.title = `SHA256: ${release.sha256}`;
    heading.dataset.zh = 'Windows 完整安装预览版';
    heading.dataset.en = 'Complete Windows installer preview';
    paragraph.dataset.zh = `版本 ${release.version}，${(release.bytes / 1048576).toFixed(2)} MiB。完整安装包包含本次发行的组件及依赖安装器；仍为预览版，请先在隔离环境验证。`;
    paragraph.dataset.en = `Version ${release.version}, ${(release.bytes / 1048576).toFixed(2)} MiB. The complete installer includes this release's components and prerequisites. This is a preview; test it in an isolated environment first.`;
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
  load().catch(() => { /* 元数据不可用时保留源码入口，不提供无法验证的下载。 */ });
})();
