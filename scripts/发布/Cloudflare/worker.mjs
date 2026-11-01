const manifestPattern = /^\/v1\/(stable|preview)\/(win-x64|win-arm64)\/manifest\.json$/;
const objectPattern = /^\/objects\/([a-f0-9]{64})\/([A-Za-z0-9][A-Za-z0-9._-]{0,180})$/;

export default {
  async fetch(request, env) {
    if (!['GET', 'HEAD'].includes(request.method)) return new Response('Method not allowed', { status: 405, headers: { Allow: 'GET, HEAD' } });
    const url = new URL(request.url);
    const manifest = manifestPattern.exec(url.pathname);
    const artifact = objectPattern.exec(url.pathname);
    if (!manifest && !artifact) return new Response('Not found', { status: 404 });
    const key = manifest ? `channels/${manifest[1]}/${manifest[2]}.json` : url.pathname.slice(1);
    if (!env.RELEASES) return new Response('Release storage not configured', { status: 503 });
    // 清单短缓存，对象以哈希命名；指针只能在文件上传并校验后切换。
    const conditional = new Headers(request.headers);
    if (artifact && conditional.has('range') && conditional.has('if-range')) {
      const current = await env.RELEASES.head(key);
      if (!current) return new Response('Not found', { status: 404, headers: { 'Cache-Control': 'no-store' } });
      // 续传只接受当前强 ETag；实体变化时完整返回，避免把旧片段与新内容拼接。
      if (conditional.get('if-range') !== current.httpEtag) conditional.delete('range');
      conditional.delete('if-range');
    }
    const options = artifact ? { range: conditional, onlyIf: conditional } : { onlyIf: conditional };
    const object = request.method === 'HEAD' ? await env.RELEASES.head(key) : await env.RELEASES.get(key, options);
    if (!object) return new Response('Not found', { status: 404, headers: { 'Cache-Control': 'no-store' } });
    const headers = new Headers();
    object.writeHttpMetadata?.(headers);
    headers.set('ETag', object.httpEtag);
    headers.set('X-Content-Type-Options', 'nosniff');
    headers.set('Cache-Control', manifest ? 'public, max-age=60' : 'public, max-age=31536000, immutable');
    headers.set('Content-Type', manifest ? 'application/json; charset=utf-8' : 'application/octet-stream');
    headers.set('Accept-Ranges', artifact ? 'bytes' : 'none');
    if (request.method === 'HEAD') {
      headers.set('Content-Length', String(object.size));
      return new Response(null, { status: 200, headers });
    }
    if (!('body' in object)) return new Response(null, { status: 304, headers });
    if (artifact && object.range) {
      const start = object.range.offset ?? Math.max(0, object.size - object.range.suffix);
      const length = object.range.length ?? object.range.suffix;
      headers.set('Content-Range', `bytes ${start}-${start + length - 1}/${object.size}`);
      headers.set('Content-Length', String(length));
      return new Response(object.body, { status: 206, headers });
    }
    headers.set('Content-Length', String(object.size));
    return new Response(object.body, { status: 200, headers });
  }
};
