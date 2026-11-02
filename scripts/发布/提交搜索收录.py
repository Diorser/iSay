"""准备 IndexNow 站点验证文件，发布后核验线上所有权再通知搜索引擎。"""
from pathlib import Path
from urllib.parse import urlparse
from urllib.request import Request, urlopen
from urllib.error import HTTPError
import argparse
import json
import secrets
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[2]
DIST = ROOT / 'website/dist'
STATE = ROOT / 'artifacts/搜索收录'
CONFIG = STATE / 'IndexNow本机配置.json'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument('--prepare', action='store_true', help='生成公开站点验证文件，随后部署官网')
    action.add_argument('--submit', action='store_true', help='核验线上验证文件与页面，再提交更新通知')
    args = parser.parse_args()
    facts = json.loads((DIST / 'product.json').read_text(encoding='utf-8'))
    base = facts['website'].rstrip('/')
    address = urlparse(base)
    if address.scheme != 'https' or address.path or address.query or address.fragment or address.username or address.port:
        raise ValueError('产品资料必须提供不带路径和凭据的 HTTPS 官网地址。')
    STATE.mkdir(parents=True, exist_ok=True)
    if CONFIG.exists():
        config = json.loads(CONFIG.read_text(encoding='utf-8'))
        if config['host'] != address.hostname:
            raise ValueError('官网域名已更改，请归档本机配置后为新域名重新准备验证文件。')
    else:
        if args.submit:
            raise ValueError('请先执行 --prepare，部署生成的站点验证文件后再提交。')
        key = secrets.token_hex(16)
        config = {'host': address.hostname, 'key': key, 'keyLocation': base + '/indexnow-' + key + '.txt'}
        CONFIG.write_text(json.dumps(config, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    key_file = DIST / Path(urlparse(config['keyLocation']).path).name
    if args.prepare:
        key_file.write_text(config['key'], encoding='utf-8')
        print('IndexNow 验证文件已准备；请先部署 website/dist，再执行 --submit。本机配置保存在 artifacts/搜索收录。')
        return

    # 验证文件只用于该站点公开所有权，不是 Cloudflare 或其他服务的访问凭据。
    with urlopen(config['keyLocation'], timeout=30) as response:
        if response.read(1024).decode('utf-8-sig').strip() != config['key']:
            raise ValueError('线上站点验证文件内容不匹配，尚未提交。')
    sitemap = ET.fromstring((DIST / 'sitemap.xml').read_text(encoding='utf-8'))
    urls = [node.text for node in sitemap.findall('{*}url/{*}loc')]
    if not urls or len(urls) != len(set(urls)) or len(urls) > 10000:
        raise ValueError('站点地图 URL 为空、重复或超过单次提交上限。')
    for url in urls:
        target = urlparse(url)
        if target.scheme != 'https' or target.netloc != address.netloc or target.query or target.fragment:
            raise ValueError('仅允许提交本站规范 URL。')
        with urlopen(url, timeout=30) as response:
            if response.status != 200 or urlparse(response.url).netloc != address.netloc:
                raise ValueError('规范产品页线上不可用，尚未提交。')
    body = json.dumps({**config, 'urlList': urls}).encode('utf-8')
    request = Request('https://api.indexnow.org/indexnow', data=body,
                      headers={'Content-Type': 'application/json; charset=utf-8', 'User-Agent': 'iSay-site-publisher/1.0'}, method='POST')
    try:
        with urlopen(request, timeout=45) as response:
            status = response.status
    except HTTPError as error:
        status = error.code
    report = {'timeUtc': datetime.now(timezone.utc).isoformat(), 'host': config['host'],
              'urls': urls, 'httpStatus': status, 'received': status in (200, 202),
              'indexingVerified': False, 'note': '成功响应仅代表收到通知，不代表已抓取、收录或获得排名。'}
    (STATE / '最近提交结果.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    if not report['received']:
        raise RuntimeError(f'IndexNow 返回 HTTP {status}，请查看官方文档排查，不要连续重复提交。')
    print(f'IndexNow 已收到 {len(urls)} 个规范页面的更新通知（HTTP {status}）；尚未验证实际收录或排名。')


if __name__ == '__main__':
    main()
