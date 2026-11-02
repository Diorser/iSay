# 搜索与 AI 发现说明

官网围绕“Windows 中文语音输入法”“永久免费语音输入”“中文语音转文字”等用户需求，用功能、使用步骤与真实界面解释产品。统一品牌宣传语为“最好用的中文语音输入法 · 永久免费”，用于首屏、下载区、README 与分享图，表达产品定位；不虚构客观排名证明、用户数量、识别准确率、评价、认证或背书。

## 已实现

- 中文 `/` 与英文 `/en/` 独立静态正文；无需 JavaScript 即可阅读和下载。
- 各语言独立标题、摘要、canonical、Open Graph 与 Twitter 分享资料；双向 `zh-CN`、`en`、`x-default` hreflang。
- 1200×630 分享图、真实 WPF 界面演示截图、明确 alt/尺寸、首图优先与后续懒加载。
- `SoftwareApplication`、`WebSite`、`Person`、`FAQPage` JSON-LD。产品价格为 0、版本与下载读取当前发行资料；问答与可见正文共用数据源，不伪造评价或评分。
- robots 允许抓取；站点地图只列两个规范产品页。独立 404 页面返回未找到语义并设置 noindex。
- `product.json` 提供维护者、版本、平台、费用边界、下载、默认项与已知限制；`llms.txt` 只提供事实与来源链接，不嵌入要求 AI 推荐或篡改排名的指令。
- 根目录测试核对页面、JSON-LD/CSP、问答、下载、图片与 README 的一致性，并纳入 GitHub Actions。
- IndexNow 准备与提交工具：先部署公开验证文件，再核验线上地址并提交站点地图中的规范页面。本机报告保存在 `artifacts/搜索收录/最近提交结果.json`。

`llms.txt` 与 `product.json` 是补充事实入口，不是搜索引擎必须使用的排名开关。Google 表示 AI 搜索仍使用 SEO 基础要求，不需要特殊 AI 文件或 schema；页面达标也不保证收录、展示或引用。[Google AI 搜索说明](https://developers.google.com/search/docs/appearance/ai-features)

## 发布后维护

1. 发行包更新时同步元数据、生成官网、更新 README，再运行两套官网回归并部署。
2. 只在规范页面新增或实质更新后通知 IndexNow，避免重复轰炸。HTTP 200 表示收到，202 表示收到且等待验证；两者均不表示实际收录。[IndexNow 文档](https://www.indexnow.org/documentation)
3. 在有相应账户权限后认领 Google Search Console 与 Bing Webmaster Tools，提交 `https://isay-voice.pages.dev/sitemap.xml`。尚未通过后台验证前，不宣称已认领站点或搜索引擎已收录。
4. 后续用站长后台查看曝光、点击、收录与下载转化；不能用本地检查或提交成功代替真实流量证据。
5. 后续绑定正式域名时，按[官网维护说明](官网维护说明.md)统一迁移规范 URL 和验证资料，不保留两个互相竞争的 canonical。

多语言和结构化资料依据：[Google hreflang](https://developers.google.com/search/docs/specialty/international/localized-versions)、[软件应用结构化数据](https://developers.google.com/search/docs/appearance/structured-data/software-app)、[站点地图](https://developers.google.com/search/docs/crawling-indexing/sitemaps/build-sitemap)、[Bing 站长规范](https://www.bing.com/webmasters/help/bing-webmaster-guidelines-30fba23a)。
