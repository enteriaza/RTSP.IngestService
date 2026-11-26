# HLS output over HTTP

The HTTP HLS origin (`Http/`) serves the live HLS media playlists, fMP4 initialization segments and fMP4 media
segments that HLS packaging produces. This page covers what a browser player needs: URLs, media types, caching,
security headers and CORS. The implementation is described in
[architecture.md](architecture.md#http-hls-origin).

**HLS is currently served over HTTP. CORS and security headers do not encrypt media traffic. Anyone able to observe the network path may be able to inspect the stream. HTTPS/TLS remains a planned security enhancement.**

## Resources

| Resource | URL | Content-Type | Cache-Control |
|----------|-----|--------------|---------------|
| Media playlist | `/hls/{application}/{stream}/index.m3u8` | `application/vnd.apple.mpegurl` | `no-cache` |
| Initialization segment | `/hls/{application}/{stream}/{generation}/init-{n}.mp4` | `video/mp4` | `public, max-age=L` |
| Media segment | `/hls/{application}/{stream}/{generation}/segment-{n}.m4s` | `video/iso.segment` | `public, max-age=L` |
| 404, 405, 500 | | none | `no-store` |

- These are the only formats served. There is no master (multivariant) playlist, no MPEG-TS segment, no subtitle or
  key file, and no byte-range playlist. `{stream}` is the publisher's non-secret `Name`, never the stream key.
- `GET` and `HEAD` only. `HEAD` returns the headers and `Content-Length` without a body. Other methods are 405 with
  `Allow: GET, HEAD`, apart from CORS preflights when CORS is enabled (below).
- Every resource has an `ETag`; `If-None-Match` returns 304. There is no `Last-Modified`.
- `Range` is accepted from browsers but ignored: responses are always the whole resource (200, `Accept-Ranges: none`).
  Segments are bounded by `Hls:MaxSegmentSize`, and players fetch them whole.

### Caching

- **Playlist:** `no-cache`. A cache may store it but must revalidate every time, so a live refresh is never stale; an
  unchanged playlist costs a 304.
- **Segments:** `public, max-age=L`, where `L` = `EndedStreamRetention` + `MaxSegmentDuration` × (2 × max(`PlaylistWindowSize`, 4) + 1).
  With the defaults, `L` is 160 s. Segment URLs include a random per-process generation, so a URL never names different
  media and a cached copy is always correct.
- **Errors:** `no-store`.
- With a CORS allowlist, every HLS response also carries `Vary: Origin`, so a shared cache keeps one entry per
  origin and never serves one origin's grant to another.

## Security headers

Every HLS response carries these headers, including 304s, errors and preflights:

| Header | Value | Purpose |
|--------|-------|---------|
| `X-Content-Type-Options` | `nosniff` | Browsers use the declared media type and never reinterpret a response as something else (for example script). |
| `Referrer-Policy` | `no-referrer` | Following a link from a directly opened resource sends no referrer. |
| `X-Frame-Options` | `DENY` | A resource opened directly, which browsers render as a media document, cannot be framed by another site. It does not affect `fetch`, XHR, MSE or `<video>` loads. |
| `Cross-Origin-Resource-Policy` | `cross-origin` (configurable) | Lets pages on other origins embed the media, including pages that use cross-origin isolation (`Cross-Origin-Embedder-Policy`). |

These headers are deliberately absent:
- **`Content-Security-Policy`:** media responses are not documents, and a policy here could only break players.
- **`Strict-Transport-Security`:** the endpoint is HTTP-only.
- **Cookies:** none are set, so none are marked `Secure`.
- **`Cross-Origin-Opener-Policy` and `Cross-Origin-Embedder-Policy`:** these are document headers and belong on the player page.

Management routes (everything under `/health/`) keep their own headers (`no-store`, `nosniff`). They never get
CORS or `Cross-Origin-Resource-Policy`.

## CORS

A browser player hosted on another origin (for example `https://player.example.com` reading
`http://media-host:8080/hls/...`) reads the playlist and segments with `fetch` or XHR. Through hls.js or another MSE
player, that requires CORS. Native HLS in Safari's `<video src>` does not need it. CORS is **off** by default: no CORS
header is sent and `OPTIONS` is 405, exactly as before.

CORS only controls which web pages may read the responses in a browser. **It is not authentication or access
control.** Any non-browser client, and any page that can read responses without CORS, can still fetch the media.

### Configuration

`Http:Cors` is validated at startup with strict binding. An invalid setting stops the service with exit code 78 and an
error that names the setting.

| Setting | Default | Valid values |
|---------|---------|--------------|
| `Http:Cors:Enabled` | `false` | `true` or `false` |
| `Http:Cors:AllowedOrigins` | empty | Up to 100 distinct origins in serialised form (below); required when enabled, unless `AllowAnyOrigin` |
| `Http:Cors:AllowAnyOrigin` | `false` | `true` sends `Access-Control-Allow-Origin: *`; cannot be combined with `AllowedOrigins` or `AllowCredentials` |
| `Http:Cors:AllowCredentials` | `false` | `true` sends `Access-Control-Allow-Credentials: true` to listed origins |
| `Http:Cors:MaxAgeSeconds` | `600` | 0–86400; the `Access-Control-Max-Age` of granted preflights |
| `Http:CrossOriginResourcePolicy` | `cross-origin` | `cross-origin`, `same-site` or `same-origin` |

An origin must be written exactly as browsers send it in the `Origin` header: `scheme://host[:port]`, with these rules:
- The scheme is `http` or `https`, and the host is lowercase (punycode for internationalised names, brackets for IPv6).
- There is no path, trailing slash, query, fragment or user information.
- The default port is omitted.

Anything else is rejected, and the error suggests the corrected value where there is one. For example,
`https://Player.Example.com/` is rejected with "use 'https://player.example.com'". `*`, wildcard hosts such as
`https://*.example.com` and `null` are rejected too; list each origin instead. No origin is hard-coded.

Allowlist, which is the recommended mode:

```json
{
  "Http": {
    "ListenAddress": "0.0.0.0",
    "Port": 8080,
    "Cors": {
      "Enabled": true,
      "AllowedOrigins": [ "https://player.example.com", "http://localhost:5173" ]
    }
  }
}
```

The same settings as environment variables: `Http__Cors__Enabled=true`,
`Http__Cors__AllowedOrigins__0=https://player.example.com`, `Http__Cors__AllowedOrigins__1=http://localhost:5173`.

Any origin, for public streams only:

```json
{ "Http": { "Cors": { "Enabled": true, "AllowAnyOrigin": true } } }
```

With `AllowAnyOrigin`, every web page on the internet can play the stream in its visitors' browsers, and there is no
`Vary: Origin`. Use it only for media that is meant to be public. Credentials are never granted in this mode, and the
configuration is rejected if both are set.

`AllowCredentials` is only needed when the player sends cookies or HTTP authentication (`withCredentials` or
`credentials: "include"`). The origin itself has no cookies or authentication, so leave it off unless something
in front of it requires credentials.

### Behaviour

- **Allowed origin.** A `GET`, `HEAD` or 304 response gets `Access-Control-Allow-Origin` with the configured
  origin and `Access-Control-Expose-Headers: Content-Length, Content-Range, Accept-Ranges, ETag, Last-Modified`.
  The same applies to 404 and 500, so the player can read the error.
- **Unlisted origin.** The response is served as usual but has no CORS header, so the browser withholds it from the
  page. The request origin is never echoed back. A request with more than one `Origin` value is treated as unlisted.
- **Preflight.** A preflight is an `OPTIONS` request with `Origin` and `Access-Control-Request-Method`. It is answered
  with 204 and no body, before path handling and without touching the segment store. It is granted only when all of
  these hold:
  - the origin is allowed;
  - the requested method is `GET` or `HEAD`;
  - any requested header is `Range`.

  A granted preflight gets `Access-Control-Allow-Methods: GET, HEAD`, `Access-Control-Allow-Headers: Range` and
  `Access-Control-Max-Age`. Any other preflight gets the 204 with no CORS header, and the browser blocks the request.
  Every preflight carries `Vary: Origin, Access-Control-Request-Method, Access-Control-Request-Headers`.
- **Other `OPTIONS` requests,** and every `OPTIONS` request while CORS is disabled, remain 405.
- Startup logs event 8003 with the policy, for example
  `HTTP HLS origin CORS 1 allowed origins (https://player.example.com); Cross-Origin-Resource-Policy cross-origin`.

### Playing from a separately hosted page

The page below is served from `https://player.example.com`, which is listed in `AllowedOrigins`. It plays the
stream from the media host with [hls.js](https://github.com/video-dev/hls.js), and falls back to native HLS where
MSE is unavailable (Safari on iOS).

```html
<!doctype html>
<meta charset="utf-8">
<title>Live</title>
<video id="video" controls muted autoplay playsinline></video>
<script src="hls.min.js"></script>
<script>
  const src = "http://media-host:8080/hls/live/camera1/index.m3u8";
  const video = document.getElementById("video");
  if (window.Hls && Hls.isSupported()) {
    const hls = new Hls();
    hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) console.error(data.type, data.details); });
    hls.loadSource(src);
    hls.attachMedia(video);
  } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
    video.src = src;
  }
</script>
```

- With the default `Hls` settings no request needs a preflight, because hls.js sends only safelisted headers to the
  origin. A player that adds `Range` gets a granted preflight; a player that adds any other header is blocked.
- A page served over `https` cannot load `http` media: browsers block it as mixed content, whatever CORS says. Until
  the origin supports HTTPS, serve the player page over `http`, or use the browser's localhost exception during
  development.
- If the page is not in `AllowedOrigins`, the browser reports a CORS error and hls.js reports a fatal
  `manifestLoadError`.

## Limitations

- HTTP only, with no TLS, as stated above. CORS and these headers protect neither confidentiality nor integrity in
  transit.
- No authentication or per-viewer authorisation. CORS does not restrict non-browser clients.
- Origins are matched exactly. There is no wildcard subdomain matching, and the `null` origin (sandboxed iframes,
  `file:` pages) is never granted.
- No byte ranges, no `Last-Modified` and no master playlist. `Content-Range` and `Last-Modified` are exposed but not
  currently sent.
