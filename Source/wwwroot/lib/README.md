# Vendored libraries

Third-party front-end code served from `/lib`. Files are unmodified upstream builds at the pinned version. No CDN is used at runtime.

| Library | Version | License | Used by |
|---|---|---|---|
| [@popperjs/core](https://popper.js.org) | 2.11.8 | MIT | Positioning engine required by Tippy.js |
| [tippy.js](https://atomiks.github.io/tippyjs/) | 6.3.7 | MIT | Tooltips on icon buttons (`ui/_footer.html`) |

## Files

| File | Source | SHA-256 |
|---|---|---|
| `popper/popper.min.js` | https://cdn.jsdelivr.net/npm/@popperjs/core@2.11.8/dist/umd/popper.min.js | `c212f4b505a86352aed62b24a8f16f999f821ecbe6456c7f3c8a04bc87968782` |
| `popper/LICENSE` | https://cdn.jsdelivr.net/npm/@popperjs/core@2.11.8/LICENSE.md | `bf67e2c9b7974543bb18ced3a0b37f53729aaff953b442dd89a3adaf90fd93e8` |
| `tippy/tippy-bundle.umd.min.js` | https://cdn.jsdelivr.net/npm/tippy.js@6.3.7/dist/tippy-bundle.umd.min.js | `3f0fe70eb26ccf28f6887a192e29d38dd7ef7c2f079a73304ad42ddc7bed37de` |
| `tippy/LICENSE` | https://cdn.jsdelivr.net/npm/tippy.js@6.3.7/LICENSE | `7f4929c55f525ae510a77c361496c5a86a092434a3681c95a4dc89e951c67e98` |

## Rules

- Formatters and linters must skip this directory. A reformatted file no longer matches its hash.
- Update: download the new version from the same source URL, replace the file, update version and hash in this file.
- Verify: `sha256sum popper/* tippy/*` from this directory.
