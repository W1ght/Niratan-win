# Third-party notices

## Video metadata services

Niratan queries these services on demand after explicit user consent. It does not mirror their databases, does not upload media/sidecar content, and retains source links with normalized metadata. Each service remains governed by its current terms:

- TMDB: movie/TV text and artwork. This product uses the TMDB API but is not endorsed or certified by TMDB. https://www.themoviedb.org/ and https://developer.themoviedb.org/docs/faq
- TVmaze: TV text, episodes and artwork under the API's CC BY-SA attribution terms. https://www.tvmaze.com/api
- AniList: on-demand anime identity, aliases, tags and cross IDs; images disabled by default. https://anilist.gitbook.io/anilist-apiv2-docs/docs/guide/terms-of-use
- AniDB: weekly official title index only; no unregistered real-time detail client. https://wiki.anidb.net/API
- Bangumi: on-demand anime/Japanese drama supplemental text; images disabled by default. https://bangumi.github.io/api/
- TheTVDB: adapter and offline fixtures are present, but production use remains disabled until Niratan receives project authorization. https://thetvdb.com/api-information

No Jikan/MAL HTML scraping, OpenSubtitles, Fanart.tv or AniBridge dataset is used in this phase.

## MonoTorrent 3.0.2

- Project: https://github.com/alanmcgovern/monotorrent
- Package: https://www.nuget.org/packages/MonoTorrent/3.0.2
- License: MIT
- Use in Niratan: in-process BitTorrent download engine for explicitly selected Nyaa RSS results. Niratan stops the torrent after download completion and does not expose a general-purpose seeding or tracker-management UI.

Copyright (c) 2006-2024 Alan McGovern and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## YoutubeExplode 6.6.0

- Project: https://github.com/Tyrrrz/YoutubeExplode
- Package: https://www.nuget.org/packages/YoutubeExplode/6.6.0
- License: MIT
- Use in Niratan: in-process YouTube metadata, stream-manifest, and publisher-caption resolution. `YoutubeExplode.Converter`, yt-dlp, youtube-dl, Deno, Node, helper downloads, and child processes are not used.

Copyright (c) 2017 Alexey Golub

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
