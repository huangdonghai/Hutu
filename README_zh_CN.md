# Hutu

[English](README.md) | **简体中文**

一个用 C# 编写、面向 .NET 10 的静态 HTML5 / CSS3 渲染引擎。Hutu 解析文档、计算样式、对盒树排版，
最后交给宿主一份与平台无关的绘制指令列表。

它是渲染器，不是浏览器：没有 JavaScript 引擎，没有 DOM 脚本，除加载文档外也没有导航状态。
真正的绘制由宿主后端完成。

### 主要特性

- **融合式构建**：直接从 DOM 完成选择器匹配、层叠与样式计算并生成盒树，不另建渲染树，
  也不为每个元素保留声明对象。
- **两阶段排版**：`Measure` 只回答「这个盒想有多大」，`Arrange` 把尺寸固定成矩形并生成
  `LayoutBox` 树。Flex、Grid、表格、多栏因此可以「先问尺寸、再分位置」。
- **完整的文本排版**：块级与行内格式化、bidi、竖排书写模式、断行、`text-spacing-trim`、
  `text-autospace`、`hanging-punctuation`、ruby，以及行间注、边注。
- **分页介质**：只排一次「长文档」，由 `PageSequence` 切成页，每页不重新排版。
- **宿主无关的内核**：`HutuViewInternal` 接受注入的 `ITextMeasurer` / `IImageLoader` /
  `IFontLoader`；随仓库提供的后端基于 SkiaSharp + HarfBuzz + Svg.Skia。
- **GPU 直出**：Avalonia 宿主通过 `ISkiaSharpApiLeaseFeature` 租用平台 `SKCanvas`，
  每帧把 paint list 提交给 GPU。

> 状态：实验性项目。目前以约 8,300 对借用的 reftest 语料跟踪一致性，
> 见 [docs/reftest-scoring_zh_CN.md](docs/reftest-scoring_zh_CN.md)。

### 渲染管线

```
AngleSharp DOM
  -> BoxBuilder.TryBuildFused      （融合样式计算 + 盒树）
  -> LayoutEngine.Layout           （Measure -> Arrange）
  -> LayoutBox 树                  （文档绝对坐标）
  -> Painter.Paint                 （PaintList：Commands / Fixed / Sticky）
  -> SkiaPaintExecutor.Execute     （平台画布，带可见性剔除）
  -> PageSequence.Build            （仅分页介质，切分长文档）
```

### 目录结构

| 路径 | 内容 |
| --- | --- |
| `src/Hutu/` | 内核；`HutuViewInternal` 是公开入口。 |
| `src/Hutu.Skia/` | Skia 后端：`SkiaTextMeasurer`、`SkiaPaintExecutor`、图片/字体加载器。 |
| `src/Hutu.Avalonia/` | `HutuView` 控件与 GPU 绘制操作。 |
| `src/Hutu.Maui/`、`src/Hutu.Uno/`、`src/Hutu.MewUi/` | 其它宿主壳工程。 |
| `src/Hutu.Tests/`、`src/Hutu.Avalonia.Tests/` | 单元测试。 |
| `src/Hutu.Conformance.Tests/` | reftest 框架（借用语料 + 手写逐像素断言）。 |
| `samples/` | Avalonia 示例程序（Desktop / Browser / Android / iOS）与示例页面。 |
| `conformance/reftests/` | 借用的 WPT、Gecko 语料，以及手写用例。 |
| `tools/Hutu.Reference/` | 用 Playwright 采集浏览器几何数据作为参照的工具。 |
| `deps/` | 内置的 AngleSharp / AngleSharp.Css 源码（项目引用，就地修补）。 |
| `docs/` | 设计与状态说明。 |
| `build/` | 临时脚本、探针页面与 reftest 计分报告，不是构建产物目录。 |

### 快速开始

前置条件：**.NET 10 SDK**。Skia / HarfBuzz 原生库由 NuGet 包提供，无需单独安装系统依赖。

```bash
dotnet build Hutu.slnx
dotnet run --project samples/Sample.Avalonia.Desktop/Sample.Avalonia.Desktop.csproj
dotnet test src/Hutu.Tests/Hutu.Tests.csproj
```

SkiaSharp、Avalonia.Skia 与 HarfBuzz 的版本在 `Directory.Packages.props` 中统一锁定，
不要单独升级其中某一项。

### 在 Avalonia 应用里使用

```csharp
using Hutu.Avalonia;

var view = new HutuView();          // 自带测量器、执行器与加载器
window.Content = view;
await view.SetHtml("<p style='color:teal'>Hello from Hutu</p>");
// 或者：await view.LoadUrlAsync("https://example.com/");
```

`HutuView` 是一个 `ScrollViewer`，提供缩放、文本选择、链接跳转和分页模式
（`SetPaged`、`ZoomIn`、`ZoomOut`、`AddEmbeddedResourceSource` 等）。
非 Avalonia 宿主可直接构造 `HutuViewInternal`，注入自己的
`ITextMeasurer` / `IImageLoader` / `IFontLoader`，再用自己的执行器绘制 `Paint`。

### 一致性检测

```powershell
# 对借用语料计分（不做硬断言，产出 build/reftest-score.md）
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj `
  --filter "FullyQualifiedName~TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay" --no-build
```

`HUTU_SCORE_ONLY` 按路径缩小语料范围，`HUTU_SCORE_LISTED` 控制报告列出的 finding 数量，
`HUTU_PROBE_PAGE` 可查看单对页面的布局树。完整流程、基线与常见坑见
[docs/reftest-scoring_zh_CN.md](docs/reftest-scoring_zh_CN.md)。

### 设计约定

- Skia 后端每帧直接绘制到平台 GPU 画布上，内核**不做全屏缓存画面、也不做脏矩形**：
  GPU 本来就要重新合成整幅画面。性能优化的重点因此放在 CPU 侧——样式计算、排版、
  paint list 生成与指令遍历次数。`SkiaPaintExecutor.IsVisible` 的剔除要保留，
  它减少的是提交的绘制指令数，而不是填充面积。
- 修改 CSS 行为时应在说明中给出对应规范章节；改动影响排版或绘制输出时应跑 reftest
  并与基线比对。不改变输出的纯性能重构不要求分数不变。
- 文档按语言命名：简体中文文档带 `_zh_CN` 后缀（例如 `docs/css-text-4-status_zh_CN.md`），
  英文文档不带后缀。

### 延伸阅读

- [docs/reftest-scoring_zh_CN.md](docs/reftest-scoring_zh_CN.md) —— reftest 计分与回归对比
- [docs/css-text-3-status_zh_CN.md](docs/css-text-3-status_zh_CN.md) —— CSS Text 3 实现状态
- [docs/css-text-4-status_zh_CN.md](docs/css-text-4-status_zh_CN.md) —— CSS Text 4 实现状态
- [docs/css2.1-reftest-progress_zh_CN.md](docs/css2.1-reftest-progress_zh_CN.md) —— CSS 2.1 进展
- [docs/pagination-gaps_zh_CN.md](docs/pagination-gaps_zh_CN.md) —— 分页缺口与待办

### 许可

MIT，见 [LICENSE](LICENSE)。
