"""核对静态产品页、真实资源、FAQ 与结构化资料的一致性，不模拟搜索排名。"""
from pathlib import Path
from html.parser import HTMLParser
from urllib.parse import urlparse, unquote
import base64
import hashlib
import json
import struct
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
DIST = ROOT / 'website/dist'
BASE = 'https://isay-voice.pages.dev'
release = json.loads((DIST / 'downloads/windows.json').read_text(encoding='utf-8-sig'))
checks = 0


def check(ok, label):
    global checks
    if not ok:
        raise AssertionError(label)
    checks += 1


class Page(HTMLParser):
    def __init__(self, content):
        super().__init__(convert_charrefs=True)
        self.tags = []
        self.ids = []
        self.faq = []
        self.current = None
        self.part = None
        self.structured = []
        self.script = None
        self.text = []
        self.feed(content)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        self.tags.append((tag, attrs))
        if 'id' in attrs:
            self.ids.append(attrs['id'])
        if tag == 'details':
            self.current = {'q': '', 'a': ''}
        if self.current is not None and tag in ['summary', 'p']:
            self.part = 'q' if tag == 'summary' else 'a'
        if tag == 'script':
            self.script = '' if attrs.get('type') == 'application/ld+json' else None

    def handle_endtag(self, tag):
        if tag in ['summary', 'p']:
            self.part = None
        if tag == 'details' and self.current is not None:
            self.faq.append(self.current)
            self.current = None
        if tag == 'script' and self.script is not None:
            self.structured.append(self.script)
            self.script = None

    def handle_data(self, data):
        if self.script is not None:
            self.script += data
        else:
            self.text.append(data)
        if self.current is not None and self.part:
            self.current[self.part] += data


headers = (DIST / '_headers').read_text(encoding='utf-8-sig')
for language, relative in [('zh-CN', 'index.html'), ('en', 'en/index.html')]:
    text = (DIST / relative).read_text(encoding='utf-8-sig')
    page = Page(text)
    canonical = BASE + ('/' if language == 'zh-CN' else '/en/')
    meta = {a.get('name', a.get('property')): a.get('content') for t, a in page.tags if t == 'meta'}
    check(next(a['lang'] for t, a in page.tags if t == 'html') == language, '静态语言正确')
    check('Windows' in meta['description'] and len(meta['description']) > 50, '产品描述完整')
    check('noindex' not in meta['robots'], '产品页可索引')
    check(next(a['href'] for t, a in page.tags if t == 'link' and a.get('rel') == 'canonical') == canonical, '独立规范地址')
    alternates = {a['hreflang']: a['href'] for t, a in page.tags if t == 'link' and a.get('rel') == 'alternate'}
    check(alternates == {'zh-CN': BASE + '/', 'en': BASE + '/en/', 'x-default': BASE + '/'}, '双向语言入口完整')
    check(meta['og:url'] == canonical and meta['og:locale'] in ['zh_CN', 'en_US'], '分享地址与语言正确')
    check(urlparse(meta['og:image']).path.endswith('/social-preview.png') and meta['twitter:card'] == 'summary_large_image', '大图分享卡片')
    check(len([t for t, a in page.tags if t == 'h1']) == 1, '唯一一级标题')
    check(len(page.ids) == len(set(page.ids)), '区块编号不重复')
    check(len(page.faq) == 10 and all(q['q'] and q['a'] for q in page.faq), '十条可直接读取的完整问答')
    check(len(page.structured) == 1, '结构化数据唯一')
    structured = json.loads(page.structured[0])
    graph = structured['@graph']
    software = next(g for g in graph if g['@type'] == 'SoftwareApplication')
    check(software['softwareVersion'] == release['version'] and software['downloadUrl'] == release['url'], '版本和下载跟随实际发行')
    check(software['isAccessibleForFree'] and software['offers']['price'] == '0', '免费承诺与产品一致')
    check('aggregateRating' not in software and 'review' not in software, '不虚构评分与评价')
    faq = next(g for g in graph if g['@type'] == 'FAQPage')['mainEntity']
    check([(q['name'], q['acceptedAnswer']['text']) for q in faq] == [(q['q'], q['a']) for q in page.faq], '结构化问答与可见内容逐条一致')
    digest = base64.b64encode(hashlib.sha256(page.structured[0].encode()).digest()).decode()
    check("'sha256-" + digest + "'" in headers and "script-src 'self' 'unsafe-inline'" not in headers, 'JSON-LD允许哈希与严格脚本策略')
    download_links = [a for t, a in page.tags if t == 'a' and 'data-download' in a]
    check(len(download_links) == 2 and all(a['href'] == release['url'] for a in download_links), '无需脚本也能直接下载完整安装器')
    images = [a for t, a in page.tags if t == 'img' and '/screenshots/' in a.get('src', '')]
    check(len(images) == 5 and all(a.get('alt') and a.get('width') and a.get('height') for a in images), '五张实图有说明与占位尺寸')
    check(images[0]['loading'] == 'eager' and all(a['loading'] == 'lazy' for a in images[1:]), '首屏优先、后续图片延迟加载')
    for tag, attrs in page.tags:
        for attribute in ['href', 'src']:
            if attribute not in attrs:
                continue
            url = urlparse(attrs[attribute])
            if url.scheme and url.netloc != 'isay-voice.pages.dev':
                if attribute == 'src':
                    check(False, '资源不依赖外部字体或脚本')
                continue
            if url.fragment and not url.path:
                check(url.fragment in page.ids, '内部锚点存在')
                continue
            path = unquote(url.path).lstrip('/')
            if not path:
                continue
            target = DIST / path
            if url.path.endswith('/'):
                target /= 'index.html'
            check(target.is_file(), '引用的站内资源真实存在：' + path)

facts = json.loads((DIST / 'product.json').read_text(encoding='utf-8'))
check(facts['version'] == release['version'] and facts['downloadUrl'] == release['url'] and facts['sha256'] == release['sha256'], 'AI产品资料与真实发行完全一致')
check(facts['price']['app'] == 0 and len(facts['limitations']) >= 5, '费用边界与当前限制保留')
llms = (DIST / 'llms.txt').read_text(encoding='utf-8')
check(release['version'] in llms and '/product.json' in llms and 'not an independently verified ranking' in llms, 'AI文本资料指向原始依据并正确表达品牌目标')
locations = [e.text for e in ET.parse(DIST / 'sitemap.xml').getroot().findall('{*}url/{*}loc')]
check(locations == [BASE + '/', BASE + '/en/'], '站点地图只列可索引规范产品页')
check(BASE + '/sitemap.xml' in (DIST / 'robots.txt').read_text(encoding='utf-8'), '爬虫可发现站点地图')
for image in (DIST / 'assets/screenshots').glob('*.png'):
    data = image.read_bytes()
    check(data[:8] == b'\x89PNG\r\n\x1a\n', '图片为真实PNG')
    dimensions = struct.unpack('>II', data[16:24])
    check(dimensions == ((1200, 630) if image.name == 'social-preview.png' else (1100, 760)), '截图与分享图尺寸正确')
    check(len(data) < 150000, '宣传图片体积受控')
for readme in ['README.md', 'README.en.md']:
    text = (ROOT / readme).read_text(encoding='utf-8-sig')
    check(release['url'] in text and release['version'] in text, 'README链接当前完整安装包')
    check('main-zh.png' not in text and 'refine-zh.png' not in text, 'README无过期截图占位')
check('noindex' in (DIST / '404.html').read_text(encoding='utf-8'), '不存在页面不参与索引')
print(f'官网SEO/GEO与资源回归通过：{checks}项。未模拟真实搜索收录或排名。')
