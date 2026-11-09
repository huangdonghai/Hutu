# Hutu

**English** | [简体中文](README_zh_CN.md)

A static HTML5 / CSS3 rendering engine written in C# for .NET 10. Hutu parses a document, resolves
styles, lays out the box tree, and hands the host a platform-neutral list of drawing commands.

It is a renderer, not a browser: there is no JavaScript engine, no DOM scripting, and no navigation
state beyond loading a document. The drawing itself is done by a host backend.

### Highlights

- **One fused build**: matching, cascading and computing styles directly from the DOM into a box tree;
  no separate render tree or per-element declaration objects.
- **Two-phase layout**: `Measure` answers how large a box wants to be; `Arrange` fixes that size into
  a rectangle and produces the `LayoutBox` tree. Flex, grid, table and multicol ask children for
  their sizes before handing out positions.
- **Full text layout**: block and inline formatting, bidi, vertical writing modes, line breaking,
  `text-spacing-trim`, `text-autospace`, `hanging-punctuation`, ruby and interline/margin notes.
- **Paged media**: one tall layout is sliced into pages by `PageSequence`; pages are not laid out
  again.
- **Backend-agnostic core**: `HutuViewInternal` takes an `ITextMeasurer`, `IImageLoader` and
  `IFontLoader`. The shipped backend is SkiaSharp + HarfBuzz + Svg.Skia.
- **GPU-first drawing**: the Avalonia host leases the platform `SKCanvas` through
  `ISkiaSharpApiLeaseFeature` and submits the paint list to the GPU each frame.

> Status: experimental. Conformance is tracked against ~8,300 borrowed reftest pairs; see
> [docs/reftest-scoring_zh_CN.md](docs/reftest-scoring_zh_CN.md) (Chinese).

### Pipeline

```
AngleSharp DOM
  -> BoxBuilder.TryBuildFused      (fused style resolution + box tree)
  -> LayoutEngine.Layout           (Measure -> Arrange)
  -> LayoutBox tree                (geometry, in document coordinates)
  -> Painter.Paint                 (PaintList: Commands / Fixed / Sticky)
  -> SkiaPaintExecutor.Execute     (platform canvas, with visibility culling)
  -> PageSequence.Build            (paged media only, slices the tall layout)
```

### Repository layout

| Path | What |
| --- | --- |
| `src/Hutu/` | Core engine. `HutuViewInternal` is the public entry point. |
| `src/Hutu.Skia/` | Skia backend: `SkiaTextMeasurer`, `SkiaPaintExecutor`, image/font loaders. |
| `src/Hutu.Avalonia/` | `HutuView` control and the GPU draw operation. |
| `src/Hutu.Maui/`, `src/Hutu.Uno/`, `src/Hutu.MewUi/` | Other host shells. |
| `src/Hutu.Tests/`, `src/Hutu.Avalonia.Tests/` | Unit tests. |
| `src/Hutu.Conformance.Tests/` | Reftest harness (borrowed corpora + hand-written pixel assertions). |
| `samples/` | Avalonia sample app (Desktop / Browser / Android / iOS) and sample pages. |
| `conformance/reftests/` | Borrowed WPT and Gecko corpora, plus hand-written cases. |
| `tools/Hutu.Reference/` | Playwright collector that records browser geometry as reference data. |
| `deps/` | Vendored AngleSharp / AngleSharp.Css sources (project references, patched in place). |
| `docs/` | Design and status notes. |
| `build/` | Scratch scripts, probe pages and reftest score reports — not a build output directory. |

### Getting started

Prerequisites: **.NET 10 SDK**. Native Skia / HarfBuzz binaries come from NuGet packages; no system
installation is needed.

```bash
dotnet build Hutu.slnx
dotnet run --project samples/Sample.Avalonia.Desktop/Sample.Avalonia.Desktop.csproj
dotnet test src/Hutu.Tests/Hutu.Tests.csproj
```

SkiaSharp is pinned with Avalonia.Skia and the HarfBuzz stack in `Directory.Packages.props`; do not
bump those versions independently.

### Using it in an Avalonia app

```csharp
using Hutu.Avalonia;

var view = new HutuView();          // owns the measurer, executor and loaders
window.Content = view;
await view.SetHtml("<p style='color:teal'>Hello from Hutu</p>");
// or: await view.LoadUrlAsync("https://example.com/");
```

`HutuView` is a `ScrollViewer` with zoom, selection, link handling and paged mode
(`SetPaged`, `ZoomIn`, `ZoomOut`, `AddEmbeddedResourceSource`, ...). For a non-Avalonia host,
construct `HutuViewInternal` with your own `ITextMeasurer` / `IImageLoader` / `IFontLoader` and draw
`Paint` with your own executor.

### Conformance checks

```powershell
# Score over the borrowed corpora (no assertions, writes build/reftest-score.md)
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj `
  --filter "FullyQualifiedName~TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay" --no-build
```

`HUTU_SCORE_ONLY` narrows the corpus by path, `HUTU_SCORE_LISTED` controls how many findings are
listed, and `HUTU_PROBE_PAGE` dumps the layout tree of a single pair. The full procedure, baselines
and known pitfalls are in [docs/reftest-scoring_zh_CN.md](docs/reftest-scoring_zh_CN.md).

### Design notes

- The Skia backend draws straight onto the platform GPU canvas every frame. The engine deliberately
  keeps **no cached full-surface bitmap and no dirty rectangles** — the GPU recomposites the whole
  picture anyway. Optimisation work therefore targets CPU-side work: style resolution, layout,
  paint-list generation and command traversal. `SkiaPaintExecutor.IsVisible` culling stays, because
  it reduces the number of submitted commands rather than filled area.
- Changes to CSS behaviour should cite the relevant specification section; changes that alter layout
  or paint output should be checked against the reftest baseline. Pure performance refactors that do
  not change output do not require a score change.
- Documentation naming: Simplified Chinese documents carry a `_zh_CN` suffix (for example
  `docs/css-text-4-status_zh_CN.md`); English documents have no suffix.

### Further reading

- [docs/reftest-scoring_zh_CN.md](docs/reftest-scoring_zh_CN.md) — how to run and compare reftest scores
- [docs/css-text-3-status_zh_CN.md](docs/css-text-3-status_zh_CN.md) — CSS Text 3 status
- [docs/css-text-4-status_zh_CN.md](docs/css-text-4-status_zh_CN.md) — CSS Text 4 status
- [docs/css2.1-reftest-progress_zh_CN.md](docs/css2.1-reftest-progress_zh_CN.md) — CSS 2.1 progress
- [docs/pagination-gaps_zh_CN.md](docs/pagination-gaps_zh_CN.md) — pagination gaps and TODOs

### License

MIT — see [LICENSE](LICENSE).
