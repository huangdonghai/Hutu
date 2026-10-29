# Fonts the borrowed reftests ask for

Upstream reftests reach their fonts by an address from the suite's root - `/fonts/ahem.css`,
`/fonts/noto/cjk/NotoSansCJKjp-Regular-subset-halt.otf` - because the suite is served rather than
opened from disk. Every borrowed page has that address rewritten to a relative one that lands here,
so a page opened straight from a file system reaches the same faces a served page would.

They are taken from the same upstream revision as the corpora beside them:

- Source: https://github.com/web-platform-tests/wpt (github.com/web-platform-tests/wpt)
- Revision: 2f57f45d21482f94b36123dfcec77ae7ab859bc1

| File | What it is |
|---|---|
| `Ahem.ttf` | The suite's reference face: every glyph is a full em square, which is what makes a reftest's geometry a question about layout rather than about a typeface. |
| `ahem.css` | Upstream's `@font-face` rule, with the address made relative to this folder - a root-absolute `url()` resolves against the drive root once the page is opened from disk. |
| `noto/cjk/NotoSansCJKjp-Regular-subset-halt.otf` | A subset of Noto Sans CJK JP carrying the `halt` (alternate half widths) feature, which is how the text-spacing-trim references reach a trimmed line without asking the property under test. |
| `noto/cjk/NotoSansCJKjp-Regular-subset-chws.otf` | The same subset carrying `chws` (contextual half widths). |

Licences are the upstream ones: Ahem is public domain, and the Noto CJK subsets are under the SIL
Open Font License 1.1.
