"""从同一份产品资料生成可直接抓取的中英文官网，不依赖浏览器执行才能阅读。"""
from pathlib import Path
from html import escape
import base64
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parent
DIST = ROOT / 'dist'
BASE = 'https://isay-voice.pages.dev'
REPO = 'https://github.com/Diorser/iSay'
UPDATED = '2026-11-03'
RELEASE = json.loads((DIST / 'downloads/windows.json').read_text(encoding='utf-8-sig'))
VERSION = RELEASE['version']
SIZE_MIB = f"{RELEASE['bytes'] / 1048576:.2f}"
DOWNLOAD = RELEASE['url']
SOCIAL = BASE + '/assets/screenshots/social-preview.png'

FAQ = [
    ('iSay 是什么？', 'What is iSay?',
     'iSay 是面向 Windows 的中文语音输入法。用快捷键开始说话，在支持的输入框中输入文字，再用智能整理、个人词典和本地历史继续处理表达。',
     'iSay is a Chinese voice input app for Windows. Start speaking with a hotkey, enter text in supported fields, and work with refinement, a personal vocabulary and local history.'),
    ('真的永久免费吗？', 'Is iSay really free forever?',
     '是。iSay 桌面产品永久免费，不收取 iSay 订阅费。免费承诺覆盖产品本身；如果主动配置第三方语音或 AI 接口，服务商可能另行计费。上游服务的可用性、额度与条款由其提供方决定。',
     'Yes. The iSay desktop app is free forever, with no iSay subscription. This promise covers the app itself. Optional third-party speech or AI APIs may charge separately, and upstream availability, quotas and terms remain with their providers.'),
    ('支持哪些 Windows 系统？', 'Which Windows versions are supported?',
     '当前提供 Windows x64 安装包，要求 Windows 10 2004（系统版本 19041）及以上，也面向 Windows 11。macOS、Linux 和 Windows ARM64 暂无安装版。',
     'The installer targets Windows x64, requiring Windows 10 version 2004 (build 19041) or later, including Windows 11. Installers for macOS, Linux and Windows ARM64 are not available.'),
    ('怎么开始语音输入？', 'How do I start dictating?',
     '下载安装后，选择麦克风并确认 Windows 麦克风权限。将光标放入支持的输入框，按右 Alt 开始说话，再按一次结束。也可以使用主界面录音按钮，或在 iSay 中复制结果。',
     'Install iSay, choose your microphone and check Windows microphone permission. Focus a supported text field, press RightAlt to start, and press again to finish. You can also use the recording button or copy a result from iSay.'),
    ('中文语音输入能用在聊天、文档和邮件里吗？', 'Can I dictate into chats, documents and email?',
     '可以在支持的输入框中直接输入。不同应用的输入框兼容性会有差异；遇到问题可尝试普通文本兼容模式，或在 iSay 中查看并复制结果。',
     'Direct input works in supported text fields. Compatibility varies between apps; try plain-text compatibility mode or review and copy the result from iSay when needed.'),
    ('识别原文和整理结果都能保留吗？', 'Can I compare the transcript and refined result?',
     '可以。界面提供原文和整理结果切换，可重新整理、复制或填入原应用。整理风格与效果取决于所选服务，自定义兼容接口还可使用清晰分段。',
     'Yes. Switch between the original transcript and refined result, refine again, copy, or insert into the original app. Styles and results depend on the selected service; a compatible custom API also supports clear paragraph formatting.'),
    ('录音和转写记录会保存在哪里？', 'Where are recordings and history stored?',
     '转写历史默认保存在本机并保留 30 天，录音文件默认不保存，可在设置中调整。联网识别或整理会由对应服务处理所需内容；本地历史不代表语音始终不出本机。',
     'History is stored locally and retained for 30 days by default; audio saving is off by default. These options are configurable. Online recognition or refinement sends required content to the selected service; local history does not mean audio always stays on your device.'),
    ('需要联网或额外安装运行环境吗？', 'Do I need internet access or separate runtimes?',
     '当前内置识别与整理依赖第三方组件和服务，不承诺全功能离线使用。完整安装包携带所需的 .NET、VC++ 安装器，安装向导检测后按需安装，无需分别寻找下载。',
     'Current built-in recognition and refinement depend on third-party components and services; full offline operation is not promised. The complete installer includes .NET and VC++ prerequisites and installs them only when needed.'),
    ('当前版本与更新方式是什么？', 'What is the current version and update method?',
     f'当前为 {VERSION} 完整安装预览版。Windows 10 新机安装与真实麦克风识别仍待复验，建议先在常用软件中试用。安装器尚无 Windows 发布者签名，客户端自动更新未启用，请通过官网或 GitHub Release 手动下载。',
     f'The current release is complete installer preview {VERSION}. Clean Windows 10 installation and real microphone recognition still need acceptance testing. Try it with your usual apps first. The installer has no Windows publisher signature yet; automatic updates are not enabled, so download manually from this site or GitHub Releases.'),
    ('录音后没有文字怎么办？', 'What if recording produces no text?',
     '先用主界面的录音按钮，检查麦克风选择、Windows 麦克风权限和网络，再区分识别服务与目标输入框问题。仍有问题时，可在 GitHub Issues 提交 Windows 版本、应用版本与复现步骤，提交前移除私人内容。',
     'Try the recording button, check your microphone, Windows permissions and network, then separate recognition problems from text-field compatibility. If it persists, report your Windows and iSay versions and reproduction steps on GitHub Issues, removing private content first.'),
]


def page(language):
    en = language == 'en'
    choose = lambda zh, eng: eng if en else zh
    canonical = BASE + ('/en/' if en else '/')
    title = choose('iSay - 永久免费的 Windows 中文语音输入法｜开口，就成文',
                   'iSay — Forever-free Chinese Voice Input for Windows')
    description = choose('iSay 是永久免费的 Windows 中文语音输入法。右 Alt 一键说话，支持实时转写、智能整理、个人词典、悬浮录音条和本地历史。适用于 Windows 10/11，完整安装包免费下载。',
                         'Meet iSay, a forever-free Chinese voice input app for Windows 10 and 11. Dictate with RightAlt, refine text, build a personal vocabulary and revisit local history. Download the complete installer.')

    def t(tag, zh, eng, attrs=''):
        content = escape(choose(zh, eng)).replace('\\n', '<br>')
        return f'<{tag} {attrs} data-zh="{escape(zh, quote=True)}" data-en="{escape(eng, quote=True)}">{content}</{tag}>'

    def button(extra=''):
        return f'<a class="button {extra}" data-download href="{DOWNLOAD}">{t("span", "免费下载 Windows 版", "Download for Windows")}<span aria-hidden="true">↗</span></a>'

    def screenshot(name, zh, eng, eager=False):
        loading = 'eager' if eager else 'lazy'
        priority = ' fetchpriority="high"' if eager else ''
        return f'<a class="screenshot-link" href="/assets/screenshots/{name}.png" target="_blank" rel="noopener" aria-label="{escape(choose("查看完整界面截图", "Open full-size screenshot"))}"><img src="/assets/screenshots/{name}.png" width="1100" height="760" alt="{escape(choose(zh, eng), quote=True)}" loading="{loading}" decoding="async"{priority}></a>'

    visible_faq = '\n'.join('<details>' + t('summary', qzh, qen) + t('p', azh, aen) + '</details>' for qzh, qen, azh, aen in FAQ)
    graph = {'@context': 'https://schema.org', '@graph': [
        {'@type': 'Person', '@id': BASE + '/#author', 'name': 'Diorser', 'url': 'https://github.com/Diorser'},
        {'@type': 'WebSite', '@id': BASE + '/#website', 'name': 'iSay', 'url': BASE + '/', 'inLanguage': ['zh-CN', 'en'], 'publisher': {'@id': BASE + '/#author'}},
        {'@type': 'SoftwareApplication', '@id': BASE + '/#app', 'name': 'iSay', 'url': canonical,
         'description': description, 'applicationCategory': 'ProductivityApplication',
         'operatingSystem': 'Windows 10 2004 or later; Windows 11 (x64)', 'softwareVersion': VERSION,
         'downloadUrl': DOWNLOAD, 'fileSize': str(RELEASE['bytes']) + ' bytes', 'isAccessibleForFree': True,
         'offers': {'@type': 'Offer', 'price': '0', 'priceCurrency': 'CNY', 'url': canonical + '#download'},
         'image': SOCIAL, 'screenshot': [BASE + '/assets/screenshots/' + n + '.png' for n in ['voice-input', 'history', 'dictionary', 'settings']],
         'author': {'@id': BASE + '/#author'}, 'inLanguage': ['zh-CN', 'en'],
         'featureList': choose(['中文语音输入', '快捷键启停', '智能整理', '个人词典', '本地转写历史', '中英文界面'], ['Chinese voice input', 'Hotkey controls', 'Text refinement', 'Personal vocabulary', 'Local transcription history', 'Chinese and English UI'])},
        {'@type': 'FAQPage', '@id': canonical + '#faq', 'inLanguage': 'en' if en else 'zh-CN',
         'mainEntity': [{'@type': 'Question', 'name': choose(qzh, qen), 'acceptedAnswer': {'@type': 'Answer', 'text': choose(azh, aen)}} for qzh, qen, azh, aen in FAQ]},
    ]}
    structured = json.dumps(graph, ensure_ascii=False, separators=(',', ':')).replace('</', '<\\/')
    other = '/' if en else '/en/'
    html = f'''<!doctype html>
<html lang="{'en' if en else 'zh-CN'}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="theme-color" content="#0b3937">
<meta name="color-scheme" content="light">
<title>{escape(title)}</title>
<meta name="description" content="{escape(description, quote=True)}">
<meta name="robots" content="index,follow,max-image-preview:large">
<link rel="canonical" href="{canonical}">
<link rel="alternate" hreflang="zh-CN" href="{BASE}/">
<link rel="alternate" hreflang="en" href="{BASE}/en/">
<link rel="alternate" hreflang="x-default" href="{BASE}/">
<meta property="og:type" content="website"><meta property="og:site_name" content="iSay">
<meta property="og:title" content="{escape(title, quote=True)}"><meta property="og:description" content="{escape(description, quote=True)}">
<meta property="og:url" content="{canonical}"><meta property="og:locale" content="{'en_US' if en else 'zh_CN'}">
<meta property="og:locale:alternate" content="{'zh_CN' if en else 'en_US'}">
<meta property="og:image" content="{SOCIAL}"><meta property="og:image:width" content="1200"><meta property="og:image:height" content="630">
<meta property="og:image:alt" content="{choose('iSay：开口，就成文。永久免费的 Windows 中文语音输入法。', 'iSay: turn speech into text. Forever-free Chinese voice input for Windows.')}">
<meta name="twitter:card" content="summary_large_image"><meta name="twitter:title" content="{escape(title, quote=True)}">
<meta name="twitter:description" content="{escape(description, quote=True)}"><meta name="twitter:image" content="{SOCIAL}">
<link rel="icon" type="image/png" href="/assets/iSay.png"><link rel="apple-touch-icon" href="/assets/iSay.png">
<link rel="stylesheet" href="/assets/site.css?v=20261103-product1">
<script src="/assets/site.js?v=20261103-product1" defer></script><script src="/assets/downloads.js?v=20261103-product1" defer></script>
<script type="application/ld+json">{structured}</script>
</head>
<body>
{t('a', '跳到正文', 'Skip to content', 'class="skip" href="#main"')}
<header class="header"><a class="brand" href="{canonical}" aria-label="iSay"><img src="/assets/iSay.png" width="34" height="34" alt=""><span>iSay</span></a>
<nav aria-label="{choose('主导航', 'Main navigation')}">{t('a', '功能', 'Features', 'href="#features"')}{t('a', '如何使用', 'How it works', 'href="#start"')}{t('a', '常见问题', 'FAQ', 'href="#faq"')}<a class="language" href="{other}" lang="{'zh-CN' if en else 'en'}" aria-label="{choose('Switch to English', '切换到中文')}">{choose('EN', '中文')}</a></nav></header>
<main id="main">
<section class="hero" aria-labelledby="hero-title"><div class="hero-inner">
<div class="hero-top entrance">{t('span', '开口，就成文。', 'YOUR VOICE. YOUR WORDS.', 'class="eyebrow"')}{t('span', f'Windows · 安装预览 {VERSION}', f'Windows · Installer preview {VERSION}', 'class="edition" data-release-edition')}</div>
<div class="hero-layout"><div class="hero-copy entrance"><h1 class="wordmark" id="hero-title">iSay<span class="brand-dot">.</span></h1>
{t('h2', '你的下一句话，\\n不必一字一字敲。', 'Your next thought.\\nWithout all the typing.')}
{t('p', '永久免费的 Windows 中文语音输入法。按下右 Alt，自然说出来，让文字跟上你的思路。', 'Forever-free Chinese voice input for Windows. Press RightAlt and speak naturally. Let your words keep up with your thoughts.', 'class="hero-description"')}
<div class="hero-action">{button('light')}{t('p', 'Windows 10 / 11 · x64 · 无需购买 iSay 订阅', 'Windows 10 / 11 · x64 · No iSay subscription', 'class="download-hint"')}</div>
<a class="text-link on-dark" href="#features">{t('span', '看看它能做什么', 'See what you can do')}<span aria-hidden="true">↓</span></a></div>
<figure class="hero-product entrance">{screenshot('voice-input', 'iSay Windows 中文语音输入真实界面，展示会议通知整理结果与右 Alt 快捷键', 'The real iSay interface with a refined meeting notice and RightAlt shortcut', True)}{t('figcaption', '真实界面，内容为演示数据 · 点击查看大图', 'Real interface with example content · Open full-size image')}</figure></div>
<div class="hero-bottom entrance">{t('span', '少一点敲键盘，多一点表达。', 'Less typing. More of what you mean.')}<span><kbd>右 Alt / RightAlt</kbd>{choose('按一下开始，再按结束', 'Press to start. Press again to finish.')}</span></div>
</div></section>

<section class="intro section" id="features" aria-labelledby="intro-title"><div class="section-heading reveal">{t('span', '01 / 每天都用得上的语音输入', '01 / MADE FOR EVERYDAY WRITING', 'class="eyebrow"')}{t('h2', '把注意力，\\n留给想法本身。', 'Stay with the thought.\\nLet the words follow.', 'id="intro-title"')}</div>
<div class="intro-copy reveal">{t('p', '回消息、写邮件、记灵感、起草长文。iSay 把说话变成一种顺手的输入方式，陪你把脑海里的“差不多”，写成眼前的“就是它”。', 'Messages, emails, ideas and first drafts. iSay makes speaking a natural way to write, so a half-formed thought can become something worth keeping.', 'class="lead"')}<div class="use-cases">{t('span', '聊天回复', 'Chat replies')}{t('span', '邮件与文档', 'Emails and documents')}{t('span', '灵感与笔记', 'Ideas and notes')}{t('span', '给 AI 的指令', 'Prompts for AI')}</div></div>
<div class="quick-benefits reveal"><div>{t('h3', '按键就说，松弛表达', 'One key. Your own words.')}{t('p', '右 Alt 启停，也能改成自己的快捷键或按住说话。', 'Toggle with RightAlt, choose your own shortcut, or switch to hold-to-talk.')}</div><div>{t('h3', '原文在，思路也在', 'Keep the original thought.')}{t('p', '原文与整理结果随时对照，不必为一句口误重来。', 'Compare the transcript and refined result, then keep editing.')}</div><div>{t('h3', '融入你的 Windows', 'At home on Windows.')}{t('p', '支持的输入框直接填入；也能查看后复制使用。', 'Insert into supported text fields, or review and copy from iSay.')}</div></div></section>

<section class="feature-band" aria-labelledby="refine-title"><div class="feature-row section"><div class="feature-copy reveal">{t('span', '02 / 从口语，到成文', '02 / FROM SPOKEN TO WRITTEN', 'class="eyebrow"')}{t('h2', '先把意思说清楚。\\n再把文字理顺。', 'Say what you mean.\\nThen make it read well.', 'id="refine-title"')}{t('p', '不必一边组织思路，一边纠结标点。先自然说话，再按需要整理；原文仍然保留，想改的时候随时回来。', 'Speak first without getting caught up in punctuation. Refine when you need to, with the original transcript still there to compare.')}<ul>{t('li', '切换查看原文与整理结果', 'Switch between the transcript and refined result')}{t('li', '调整标点、中英间距等文字偏好', 'Tune punctuation and Chinese–English spacing')}{t('li', '重新整理、复制，或填入原应用', 'Refine again, copy, or insert into the original app')}</ul></div><figure class="feature-visual reveal">{screenshot('raw-input', 'iSay 识别原文页，包含一段会议通知口语演示，可与整理结果对照', 'iSay original transcript view with a spoken meeting-notice example')}{t('figcaption', '原文与整理结果可切换查看 · 演示数据', 'Switch between the original and refined text · Example content')}</figure></div></section>

<section class="feature-row reverse section" aria-labelledby="vocabulary-title"><div class="feature-copy reveal">{t('span', '03 / 你的词汇，你来管理', '03 / WORDS THAT ARE YOURS', 'class="eyebrow"')}{t('h2', '专业名词，\\n不该打断表达。', 'Your vocabulary.\\nYour way of speaking.', 'id="vocabulary-title"')}{t('p', '项目名称、行业术语、常用人名，都有自己的位置。把它们放进个人词典，按需要开启词汇与学习选项，积累属于自己的表达偏好。', 'Project names, technical terms and the people you mention deserve a place of their own. Manage them in your personal vocabulary and choose the learning options that suit you.')}<ul>{t('li', '添加、搜索和管理常用词', 'Add, search and manage useful terms')}{t('li', '设置优先词与纠错规则', 'Manage priority words and corrections')}{t('li', '学习选项由你决定', 'Choose your own learning preferences')}</ul></div><figure class="feature-visual reveal">{screenshot('dictionary', 'iSay 个人热词界面，展示 8 个演示词条及优先词设置', 'iSay personal vocabulary with eight example terms and priority settings')}{t('figcaption', '个人词典真实界面 · 示例词条，不含个人数据', 'Real personal vocabulary view · Example terms, no personal data')}</figure></section>

<section class="feature-band history-band" aria-labelledby="history-title"><div class="feature-row section"><div class="feature-copy reveal">{t('span', '04 / 好想法，值得留下', '04 / KEEP THE GOOD THOUGHTS', 'class="eyebrow"')}{t('h2', '刚才说过的话，\\n还可以接着用。', 'Said it once?\\nKeep it within reach.', 'id="history-title"')}{t('p', '会议通知、工作安排、写到一半的想法，不用再从头说一遍。在本机历史里找到它，看看原文，再复制到需要的地方。', 'A meeting note, a work plan, an unfinished idea. Find it in local history, review the original and copy it wherever you need it.')}<ul>{t('li', '搜索、查看和再次复制转写记录', 'Search, review and copy earlier transcriptions')}{t('li', '历史默认保留 30 天，可自行调整', 'History is retained for 30 days by default, with adjustable settings')}{t('li', '录音文件默认不保存', 'Audio file saving is off by default')}</ul></div><figure class="feature-visual reveal">{screenshot('history', 'iSay 本地历史记录，展示四条会议通知、工作安排、文章灵感与周末计划的演示记录', 'iSay local history with four example entries for meeting notes, work plans, article ideas and weekend plans')}{t('figcaption', '本地历史真实界面 · 4 条演示记录', 'Real local history view · Four example entries')}</figure></div></section>

<section class="details-section section" aria-labelledby="details-title"><div class="details-copy reveal">{t('span', '05 / 顺手，藏在细节里', '05 / THE LITTLE THINGS MATTER', 'class="eyebrow"')}{t('h2', '设定一次，\\n舒服地一直用。', 'Set it your way.\\nMake it feel natural.', 'id="details-title"')}{t('p', '选择麦克风、录音时静音、自定义快捷键、中英文界面。主窗口之外，还有悬浮录音条和托盘，让输入跟着习惯走。', 'Choose a microphone, mute playback while recording, customize your shortcut, or switch the UI language. A floating recording bar and tray controls keep everyday input close at hand.')}<p class="minor">{choose('默认右 Alt 启停；登录自启与录音时静音默认开启，可在设置修改。', 'RightAlt toggle is the default. Sign-in startup and playback muting are enabled by default and can be changed.')}</p></div><figure class="settings-visual reveal">{screenshot('settings', 'iSay 设置页，展示右 Alt 录音快捷键、麦克风和录音时静音选项', 'iSay settings with RightAlt shortcut, microphone selection and playback muting')}{t('figcaption', '设置真实界面 · 演示配置', 'Real settings view · Example configuration')}</figure></section>

<section class="start-section" id="start" aria-labelledby="start-title"><div class="section"><div class="section-heading reveal">{t('span', '06 / 三步就能开始', '06 / THREE STEPS TO START', 'class="eyebrow"')}{t('h2', '把第一次使用，\\n变得简单一点。', 'Your first words.\\nJust a few steps away.', 'id="start-title"')}</div><ol class="steps reveal"><li><span class="step-number">01</span>{t('h3', '下载，跟着向导安装', 'Download and install')}{t('p', '完整安装包已包含环境安装器，检测后按需安装，不用分别寻找下载。', 'The complete installer includes required runtimes and installs them when needed.')}</li><li><span class="step-number">02</span>{t('h3', '选好你的麦克风', 'Choose your microphone')}{t('p', '打开 iSay，确认 Windows 允许麦克风访问，选择常用设备。', 'Open iSay, check Windows microphone permission and choose your usual device.')}</li><li><span class="step-number">03</span>{t('h3', '按右 Alt，开始说', 'Press RightAlt and speak')}{t('p', '在支持的输入框中开始说话，再按一次结束；也能在主界面查看、复制结果。', 'Speak into a supported text field and press again to finish, or review and copy the result from iSay.')}</li></ol></div></section>

<section class="free-section section" id="free" aria-labelledby="free-title"><div class="free-heading reveal">{t('span', '永久免费', 'FREE. FOREVER.', 'class="eyebrow"')}{t('h2', '好用的语音输入，\\n不该有订阅门槛。', 'A better way to write.\\nWithout an iSay subscription.', 'id="free-title"')}</div><div class="free-copy reveal">{t('p', 'iSay 桌面产品永久免费。我们想做的，是让每个人都能用得顺手的 Windows 中文语音输入法。', 'The iSay desktop app is free forever. We want speaking to feel like a natural, everyday way to write in Chinese on Windows.', 'class="lead"')}{t('p', '以成为你最好用的 Windows 中文语音输入法为目标，持续把每个细节打磨好。', 'Our goal is to make iSay your favorite Chinese voice input app on Windows, one useful detail at a time.')}<p class="minor">{choose('免费承诺适用于 iSay 产品本身。主动配置的第三方 API 可能另行收费；第三方服务的可用性、额度与条款由其提供方决定。', 'The free promise covers iSay itself. Optional third-party APIs may charge separately; upstream availability, quotas and terms remain with their providers.')}</p></div></section>

<section class="faq-section section" id="faq" aria-labelledby="faq-title"><div class="faq-heading reveal">{t('span', '用之前，你可能想知道', 'A FEW THINGS TO KNOW', 'class="eyebrow"')}{t('h2', '常见问题', 'Questions, answered.', 'id="faq-title"')}{t('p', '先了解，再放心开始。', 'Know what to expect before you begin.')}</div><div class="faq-list reveal">{visible_faq}<p class="help-link"><a href="{REPO}/issues" target="_blank" rel="noopener">{choose('还有问题？给我们反馈 ↗', 'Still have a question? Let us know ↗')}</a></p></div></section>

<section class="download-section" id="download" aria-labelledby="download-title"><div class="download-inner section"><div class="download-copy reveal">{t('span', '现在，让声音开始写字。', 'LET YOUR VOICE DO THE WRITING.', 'class="eyebrow"')}{t('h2', '开口，就成文。', 'Speak. See it written.', 'id="download-title"')}{button('light')}<p class="download-hint">Windows 10 / 11 · x64 · {VERSION} · {SIZE_MIB} MiB</p></div><div class="source-notice reveal"><h3>{choose('Windows 完整安装预览版', 'Complete Windows installer preview')}</h3><p>{choose('包含现有功能组件与所需的环境安装器。安装预览版建议先在自己的麦克风与常用软件中试用。', 'Includes the current components and prerequisite installers. Try this preview with your microphone and everyday apps first.')}</p><a class="text-link on-dark" href="{RELEASE['releaseUrl']}" target="_blank" rel="noopener">{choose('发行说明与校验文件 ↗', 'Release notes and checksums ↗')}</a></div></div></section>
</main>
<footer><a class="brand" href="{canonical}"><img src="/assets/iSay.png" width="28" height="28" alt=""><span>iSay</span></a><p>{choose('永久免费 · 为 Windows 中文表达而做', 'Free forever · Made for Chinese expression on Windows')}</p><nav aria-label="{choose('页脚导航', 'Footer navigation')}"><a href="{REPO}/blob/main/docs/公开/隐私与第三方依赖.md" target="_blank" rel="noopener">{choose('隐私说明', 'Privacy')}</a><a href="{REPO}/blob/main/docs/公开/使用与构建.md" target="_blank" rel="noopener">{choose('使用帮助', 'Help')}</a><a href="{REPO}" target="_blank" rel="noopener">GitHub ↗</a></nav><div class="footer-legal">© 2026 Diorser · {choose('自有源码 MIT 开源；第三方组件保留各自权利。', 'Own source under MIT; third-party components retain their rights.')} <a href="{REPO}/blob/main/docs/公开/完整安装包发布说明.md" target="_blank" rel="noopener">{choose('发行与依赖说明', 'Release and dependency notes')}</a> · {choose('产品资料更新', 'Product information updated')} {UPDATED}</div></footer>
</body>
</html>'''
    return html, structured


def main():
    hashes = []
    for language, relative in [('zh', 'index.html'), ('en', 'en/index.html')]:
        html, structured = page(language)
        target = DIST / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(html + '\n', encoding='utf-8')
        hashes.append("'sha256-" + base64.b64encode(hashlib.sha256(structured.encode()).digest()).decode() + "'")
    headers = (DIST / '_headers').read_text(encoding='utf-8-sig')
    headers = re.sub(r"script-src 'self'[^;]*;", "script-src 'self' " + ' '.join(hashes) + ';', headers)
    (DIST / '_headers').write_text(headers, encoding='utf-8')
    (DIST / 'robots.txt').write_text('User-agent: *\nAllow: /\nSitemap: ' + BASE + '/sitemap.xml\n', encoding='utf-8')
    (DIST / 'sitemap.xml').write_text('<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n' + ''.join(f'<url><loc>{BASE}{p}</loc><lastmod>{UPDATED}</lastmod></url>\n' for p in ['/', '/en/']) + '</urlset>\n', encoding='utf-8')
    facts = {'name': 'iSay', 'description': '永久免费的 Windows 中文语音输入法', 'website': BASE + '/', 'englishWebsite': BASE + '/en/', 'repository': REPO, 'maintainer': 'Diorser', 'updated': UPDATED, 'version': VERSION, 'releaseChannel': 'installer-preview', 'platform': ['Windows 10 2004+ x64', 'Windows 11 x64'], 'price': {'app': 0, 'currency': 'CNY', 'policy': 'iSay 桌面产品永久免费；第三方服务额度、可用性及主动配置的 API 费用由服务商决定。'}, 'downloadUrl': DOWNLOAD, 'releaseUrl': RELEASE['releaseUrl'], 'sha256': RELEASE['sha256'], 'installerBytes': RELEASE['bytes'], 'features': ['中文语音输入', 'RightAlt 快捷键启停', '智能整理与原文对照', '个人词典及纠错', '本地历史搜索与复制', '悬浮录音条及托盘', '录音时静音', '中英文界面'], 'defaults': {'hotkey': 'RightAlt', 'startup': True, 'mutePlaybackWhileRecording': True, 'saveAudio': False, 'historyRetentionDays': 30}, 'limitations': ['不承诺全功能离线使用', '输入框兼容性因应用而异', 'Win10新机安装及真实麦克风识别待复验', '安装器未签Windows发布者签名', '自动更新未启用', '第三方原生组件单独再分发授权尚未取得，不属于iSay自有MIT许可范围'], 'screenshots': {'note': f'真实{VERSION}界面，内容为演示数据。', 'urls': [BASE + '/assets/screenshots/' + n + '.png' for n in ['voice-input', 'raw-input', 'history', 'dictionary', 'settings']]}}
    (DIST / 'product.json').write_text(json.dumps(facts, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    llms = f'''# iSay

> iSay 是永久免费的 Windows 中文语音输入法。iSay is a forever-free Chinese voice input app for Windows.

## Canonical product sources
- [Chinese product page]({BASE}/): features, populated real UI screenshots, free policy, setup and FAQ.
- [English product page]({BASE}/en/): statically rendered English version.
- [Product facts]({BASE}/product.json): version, platform, pricing scope, defaults, download and known limitations.
- [Download and release notes]({RELEASE['releaseUrl']}): complete installer preview {VERSION} and SHA256.
- [Source and feedback]({REPO}): own source code, issues and dependency notices.

## Product facts
Maintainer: Diorser. Updated: {UPDATED}. Platform: Windows 10 2004+ and Windows 11, x64.
The iSay desktop app is permanently free. Optional third-party APIs may charge separately; upstream availability and quotas are not guaranteed by iSay.
Features: Chinese voice input, RightAlt controls, text refinement with original transcript comparison, personal vocabulary, local history, floating recording bar, tray, playback muting, Chinese and English UI.
History is local with 30-day default retention; audio saving is off by default. Online recognition/refinement may send required content to the selected service.
Current release is an installer preview. Clean Win10 installation and real microphone acceptance are pending. The installer is unsigned and automatic updates are not enabled.
Compatibility depends on the input field. Full offline operation is not promised. Third-party native components are outside the project's MIT license; separate redistribution authorization has not been obtained.
Screenshots show the actual released UI populated with example content, not real user records or measured recognition output.
The goal of becoming a user's favorite Windows Chinese voice input app is a brand aspiration, not an independently verified ranking. No user-count, award or accuracy-rate claims are made.
'''
    (DIST / 'llms.txt').write_text(llms, encoding='utf-8')
    not_found = '<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex,follow"><title>页面未找到 · iSay</title><link rel="stylesheet" href="/assets/site.css?v=20261103-product1"><main class="not-found"><img src="/assets/iSay.png" width="64" height="64" alt="iSay"><h1>这页走丢了。</h1><p>回到 iSay，继续把想法说出来。</p><a class="button light" href="/">返回官网 ↗</a></main></html>\n'
    (DIST / '404.html').write_text(not_found, encoding='utf-8')
    print('已生成静态中英文产品页、FAQ结构化数据、站点地图、产品资料、llms.txt及JSON-LD CSP哈希。')


if __name__ == '__main__':
    main()
