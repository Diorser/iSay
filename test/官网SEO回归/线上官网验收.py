"""核对线上官网全部公开资源与本地发布目录，保存真实 HTTP 证据。"""
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path(__file__).resolve().parents[2]
DIST = ROOT / 'website/dist'
BASE = json.loads((DIST / 'product.json').read_text(encoding='utf-8'))['website'].rstrip('/')
REPORT = ROOT / 'test/官网验收/20261103-产品化与SEO'
HEADERS = {'User-Agent': 'iSay-site-verifier/1.0', 'Cache-Control': 'no-cache'}


def fetch(path):
    with urlopen(Request(BASE + path, headers=HEADERS), timeout=30) as response:
        return response.status, response.read(), response.headers


def verify(file):
    relative = file.relative_to(DIST).as_posix()
    path = '/' + relative
    if relative == 'index.html':
        path = '/'
    elif relative.endswith('/index.html'):
        path = '/' + relative.removesuffix('index.html')
    status, content, headers = fetch(path)
    digest = hashlib.sha256(content).hexdigest()
    assert status == 200 and digest == hashlib.sha256(file.read_bytes()).hexdigest(), '线上资源与本地不一致：' + relative
    if relative in ('index.html', 'en/index.html'):
        assert 'noindex' not in headers.get('X-Robots-Tag', '').lower(), '正式产品页不得被禁止索引'
        assert "script-src 'self' 'sha256-" in headers.get('Content-Security-Policy', ''), '线上缺少 JSON-LD CSP 哈希'
    if relative == 'downloads/windows.json':
        assert 'no-store' in headers.get('Cache-Control', ''), '下载元数据不得缓存'
    return {'path': path, 'status': status, 'sha256': digest, 'bytes': len(content), 'matchesLocal': True}


def main():
    REPORT.mkdir(parents=True, exist_ok=True)
    # 所有权文件不写入公开验收报告；提交收录脚本会单独验证。
    files = [p for p in DIST.rglob('*') if p.is_file() and p.name != '_headers' and not p.name.startswith('indexnow-')]
    with ThreadPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(verify, files))
    try:
        fetch('/isay-check-page-does-not-exist')
        raise AssertionError('不存在地址未返回 HTTP 404')
    except HTTPError as error:
        assert error.code == 404 and b'noindex' in error.read(), '不存在页面状态或 noindex 错误'
    release = json.loads((DIST / 'downloads/windows.json').read_text(encoding='utf-8-sig'))
    with urlopen(Request(release['url'], headers=HEADERS, method='HEAD'), timeout=45) as response:
        assert response.status == 200 and int(response.headers['Content-Length']) == release['bytes'], '匿名安装器访问或长度不一致'
    report = {'timeUtc': datetime.now(timezone.utc).isoformat(), 'baseUrl': BASE, 'resources': results,
              'unknownPath404': True, 'anonymousInstallerHead': True, 'installerVersion': release['version'],
              'note': '资源哈希、HTTP 与匿名 HEAD 验证；不代替安装、真实麦克风或搜索引擎收录验收。'}
    (REPORT / '线上资源核验.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'线上 {len(results)} 个公开资源 SHA256 全部匹配，404、CSP、下载元数据和匿名安装器 HEAD 通过。')


if __name__ == '__main__':
    main()
