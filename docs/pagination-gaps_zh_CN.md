# Hutu 分页能力缺口调查（Paged Media）

> **进展（2026-10-30，`dev-paged` 分支）**：本报告之后分页已按 M1–M4 顺序实现；本节记录已落地的切片与最新测量，下面的"调查"部分保留为当时的缺口快照。
>
> | 口径 | 调查时 | 现在 |
> |---|---:|---:|
> | `-print` 逐页语料 | 142 / 162 | **189 / 198**（34 个 `paged` 用例从屏幕模式转入打印模式） |
> | 屏幕全量失败集 | 2138 | **2122**（0 新增失败） |
> | 单元测试 | 2128 | **2158** |
>
> 已落地（`dev-paged`，M1 期的 13 个提交在 `7357d65a..f5245f6c`）：
> - **M1**：打印媒体（`DeviceCategory.Printer`）、`@page` 的 size/margin/width/height 与页框背景/边框、`page-orientation`、页面序列（tall layout 切片）、逐页绘制与打印 runner。
> - **M2**：递归块/行分片、强制断点沿祖先传播、表格按行/行组分片、`break-inside: avoid` 的"整块推页"（含嵌套 avoid）、`overflow:hidden` 可切、浮动整块移页（avoid 浮动连边距盒保持完整）、`<br clear>`（CSS 属性与废弃属性）、flex 列向 gap 的页边切断、跨页表格重复 `thead`/`tfoot`（首片之外的每片带上表头与表脚，表头贴片顶、表脚贴片底）、`monolithic` 超高块**填满所在页**而不是在断点前提前收页（`0fad0a00`：整页都装不下的块在其所在页被切开；声明 avoid 的块仍整块移页——含 `tr` 自身的 avoid 未落到单元、行内表格的 avoid 藏在行内两处标记缺失）——`68845b18`、`357116a6`、`2436f2c3`、`3e1d4aa8`、`e7aa3963`、`d21eeb97`、`0fad0a00`；被裁剪的内容不再延长文档（`cdd35675`：`overflow:hidden` 里的内容超出盒尾后仍被收成单元，文档比布局长出 144px，多切一页——bfc-005..008 转绿）。
> - **M3**：命名页与页面组、`:first/:left/:right/:blank`、页边盒 16 个、`counter(page)`/`counter(pages)`（CSSOM 丢弃页内嵌套 at-rule，改从样式表文本扫描，`d15521ad`）；`@page` 在 `@layer` 里的层叠顺序（`23cff26c`）。
> - **M4**：浮动跨页、flex 行列分片、grid 按行分片（`6d303559`）、竖排按块轴切页与正交流页名（`035f8791`）、列向 gap；块级 clearance 与"只有格式化上下文根为浮动增高"（`0b4a83d6`）；裁剪对齐（`dd6ba4f1`）、`text-box-trim` 与分页的配合（`e9fc1ad1`）。
>
> 仍缺（未验证或需按页重排）：
> - footnote/页浮动、`print-color-adjust` 默认调色（引擎按 `exact` 绘制，与语料口径一致）、`marks/bleed`、`string-set`/`running()`——这些 Chrome 自身未实现，没有可测的对照；按报告的口径属"按产品需要排"。
> - 按用户决定记为后续里程碑（需要"按页重排"一层）：多栏跨页重排、`box-decoration-break`。
>   **多栏这条最终落点（第三次更正，已修）**：前两次假设（平衡切 monolithic 子盒、背景/列线分段）都实测为中性；用新做的打印侧定位工具（`HUTU_PRINT_LOOK`，`2cf50a54`）看出差异在第 2 页：ref 把放不下的 spanner **整块推到下一页**（页 2 留白），而我们因为多栏容器被当成 monolithic，从页中间把它切开。修法在页序列：带 spanner 的多栏容器按"节"给断点（每个 spanner 独成一片、两侧的列块各成一片，CSS Multicol 1 §6），并且**单列多栏容器不再当一整块**（没有可见的分栏可切，页应下钻到它内部的真多栏容器）。`2f1f669f` 后 `column-balancing-paged-001` 转绿，打印 188→189。
>   `box-decoration-break: clone` 不同——每片都要额外吃掉边框/内边距的高度并推动其后内容，属真正的按页重排。实测 `monolithic-overflow-032` 还需要 Blink 的"巨型分片"模型（超高 monolithic 内容所在的一片一直贯穿到内容放完，下一片紧贴其后），该测试自身的注释即写明"expected behavior here is not obvious"，故保持 ◆。`monolithic-overflow-031` 已由 `0fad0a00` 转绿。
> - 非分页特性（打印模式下同样失败）：`@scope`×3、`(scripting)` 媒体查询、表单控件外观、`mask-image`。
> - **fieldset/legend 布局（还剩 1 对，`gecko/pagination/fieldset-*`）**：这些对照是 Gecko 用例，ref 写的是 Gecko 的 legend 模型（fieldset 边框盒下移半个超出量、边框带按 legend 水平范围开缺口）——已按此实现并落地（`c334961d`：legend 提升为块盒、盒/内边距各吃一半超出量、边框开缺口、"内容恰好顶到页边时按页边切"），但只对**声明了自身块尺寸、位于普通块类父盒、不滚动、无 size containment** 的 legend 生效；auto 高度的 legend、列向、滚动容器、size-contained field 仍留在流内（这些交互各自需要独立测量：flex 基线合成、`contain: size` 的固有尺寸、定位 legend 的偏移、bidi 方向）。打印 177→188，本族 12→1 红。grid 容器的两处已落地（`78c69a8e`：`Compute` 的条目收集剔除 `IsLegend`、`Measure`/`Arrange` 让出 inset、摆放循环后把 legend 放到边框位；`LegendOf` 变成块/grid 共用的静态助手）。余下 1 对：`001-grid`。已测得原因——ref 的强制断点写在行内的 `<br class="pb">` 上（`page-break-after: always`），我们的行单元没有接到它（探针里 br 不留为行盒子），于是页 1 断在页边而不是 A 行之后；试过在 `CollectLines` 里读行内 `LineBreak` 盒的 break-after，未生效（该盒不在行盒子列表中），改动已回滚。用打印定位工具看两页后，矛盾更清楚：**ref 的第 1 页 A、B 都在**（我们的引擎忽略 `br.pb` 的断点），而 **test 的第 1 页只有 A**（`x.pb` 是块级 grid item，块级 `page-break-after` 我们照做了）。两边要相等，只有两条路：——这条路后来实做过并量过：`Line.EndedBy` → 行盒 → `CollectLines` 整条链路打通后，`001-grid` **毫无变化**，而 `00J` 与 `00J-grid` 从绿变红（净 −2），已回滚。要么让 br 的断点也生效（此前实做过，`00J`/`00J-grid` 出现回归，需先弄清 br 断点该落在哪一行），要么让页忽略 grid item 上的块级 `page-break-after`（与规范相反，不取）。此对作为一个已知的两侧矛盾记录在案。说明该 ref 的差异不只在 br 断点的消费端，需先量清 ref 在 Gecko 下的预期页断点再动。。legend 自身的 break 值按"忽略 break-before、保留 break-after"落地（`6b2842ff`，00B/00C 转绿），legend 单元前不设断点（`21b1a580`，007 转绿）。
>
> 调查日期：2026-10-30。HEAD：`7357d65a`。只读调查，未修改引擎代码。
>
> 范围：CSS Fragmentation 3 + CSS Page 3 意义上的分页——`@page` 页盒、页面序列、打印媒体、页级断点与页级绘制。宿主把一张长文档图按视口高度切片的"阅读器翻页"不在范围内；不过它同样没有 API（见 §4.4）。

## 1. 结论摘要

1. **引擎没有分页功能。** 唯一的分片实现是**多栏（multicol）内部**的分片：`Fragmentainer` / `ChildSlicer` / `FragmentSlicer` 只被 `BlockLayout` 的多栏路径与 `InlineLayout.LayoutColumns` 调用。页面、页面序列、`@page`、打印媒体、出页 API 都不存在。
2. **现有分片只到"直接子块 + 自身行"。** 多栏能切一个容器的直接子块和可断行块的行，但**嵌套块、表格、flex/grid、混合浮动、竖排都不切**；因此一旦做页面，绝大多数真实文档会在第一个嵌套 wrapper 处整块溢出。
3. **语料全绿不能证明分页。** 今天全量里 `wpt/css-page` **58/58**、`wpt/CSS2/pagination` **41/41** 全部"通过"，但每一对都在 **800×600 屏幕环境**画一张图：`@page`、`page:`、`break-*-page` 全被忽略。`page-size-013` 的 test/ref 渲染相关声明只差 `@page` 的 size/margin/width/height，却像素相同；`page-name-000` 两侧布局树逐字相同；`basic-pagination-001` 的 `break-after: page` 没有产生任何分页。这是"对称失效"型假通过，现有 `conformance/vacuous-passes_zh_CN.md` 的检测规则抓不到。
4. **缺口不是一条规则，而是四层**：页模型（@page + 页面序列 + 打印媒体）、递归分片（嵌套块/表格/flex/grid/浮动/竖排）、页级语义（命名页/左右页/空白页/页边盒/页码/固定定位）、宿主与验证（多页绘制 API + 打印模式语料）。
5. **可复用资产不算少**：断点样式读取、forced/avoid/orphans/widows 的子块与行切分、`box-decoration-break`、`LineRange`/`FragmentSlice` 的 Arrange 入口都能直接复用；缺的是"页面"这一层和"递归分片"这一能力。

## 2. 实测数据与判定口径

### 2.1 全量快照

来自 `build/reftest-score.md` 的当日全量副本 `build/_score-final.md`（2026-10-30 12:41；`build/` 不入库，重跑即可再生）：

| 语料 | 通过 | 不一致 | 画不出 |
|---|---:|---:|---:|
| wpt | 4319 / 5845（73%） | 1522 | 4 |
| gecko | 1709 / 2335（73%） | 625 | 1 |

> 当前 `build/reftest-score.md` 已被一次限定范围的运行覆盖（只有 wpt-variants），看全量数字要读 `_score-final.md` 或重跑全量。

### 2.2 分页相关语料的通过/失败

| 目录 | 对数 | 通过 | 失败 |
|---|---:|---:|---:|
| `wpt/css-page` | 58 | **58** | **0** |
| `wpt/CSS2/pagination` | 41 | **41** | **0** |
| `gecko/pagination` | 89 | 73 | 16 |
| `wpt/css-break`（含子目录） | 204 | 91 | 113 |
| `wpt/printing` | 10 | 7 | 3 |
| **全部 `-print*` 页** | **162** | **142** | **20** |

`wpt/css-break` 的 113 个失败可分为：顶层 51、`table` 34、`flexbox` 26、`grid` 2。顶层这一组本身是多栏分片的质量缺口（borders/box-shadow/box-decoration-break/out-of-flow/widows/transform/float），分页会原样继承。

`-print*` 的 20 个失败中，与分页直接相关的有：8 个 `table-fragmentation-*`、1 个 `float-with-large-margin-bottom-cross-page`、3 个 `text-box-trim/not-at-page-edges`、2 个 `gecko/pagination/float-clear`；其余为表单控件/遮罩/`@scope` 等打印差异。

### 2.3 三个 deep-look（本机实测）

| 用例 | 页面写什么 | 我们的结果 | 说明 |
|---|---|---|---|
| `wpt/css-page/page-size-013-print.html` | test 与 ref 的**渲染相关声明只差 `@page { size/margin/width/height }`** | `the two agree` | 页面几何完全没生效；这是纯 `@page` 测试，仍像素相同 |
| `wpt/css-page/page-name-000-print.html` | test 用 `page:foo/bar`，ref 用 `break-before:page` | `the two agree`，两侧布局树逐字相同 | 命名页与强制分页都没有产生任何区别 |
| `wpt/css-page/basic-pagination-001-print.html` | `div { break-after: page }`，ref 无此声明 | `the two agree`；两张 800×600 单页，`Page` 都在 `0,0` | 强制分页被忽略 |
| `wpt/css-break/table/table-fragmentation-001a-print.html` | 4in×2in 页面区域内放 3in 高的表格 | `35712 pixels differ, first at 288,0` | 表格整块留在单张屏幕页上，没有跨页切片 |

### 2.4 为什么这些"通过"不算数

`ReftestRunner.DrawAsync` 固定用 `EngineReader.WithTypefaces(800, 600)`，画一张 800×600 位图；`EngineReader` 只调用 `SetViewportSize`，没有媒体类型、没有 `@page` 尺寸、没有页面序列。也就是说：

- 分页语料中的 test/ref 都在**屏幕媒体、单页视口**里渲染；
- 任何只依赖"页面存在"的声明（`@page`、`page`、`break-*-page`、`@media print`）在两侧同时失效；
- 因此通过只能说明"两侧在屏幕模式下长得一样"，不能说明分页正确。

`conformance/vacuous-passes_zh_CN.md` 已明确说它的三条规则只能抓**单边失效**，抓不到"两侧都依赖同一个未实现特性"的对称失效。打印/分页语料是这类假通过的最大一块：58 + 41 对全绿，却没有一行分页代码。

## 3. 已有能力（可复用）

### 3.1 断点样式读取

`BreakStyle.ReadFused`（`src/Hutu/Rendering/Layout/Fragmentation/BreakStyle.cs:77`）读取：

- `break-before/after/inside` 与 legacy `page-break-*`；`always` → page，`left/right/recto/verso` 保留为 `Left/Right`；
- `widows` / `orphans`（继承，父默认 2）；
- `column-span: all`。

但 `avoid-page` 与 `avoid-column` 都折叠成同一个 `Avoid`（`:97`、`:111`），`page` 与 `column` 也不区分——分页需要把三种 fragmentainer 类型分开。

### 3.2 分片算法

- `Fragmentainer.Fill`：整子块切分，forced 断点即使还有空间也结束当前片；最后一个 fragment 至少吃一个子块，避免流停住（`src/Hutu/Rendering/Layout/Fragmentation/Fragmentainer.cs:22`）。
- `Fragmentainer.FillLines`：按行切分并保留 orphans/widows（`:51`）。
- `Fragmentainer.Balance` / `BalanceLines`：多栏平衡（`:90`、`:148`）。
- `ChildSlicer.Fitting`：子块级 forced/avoid/orphans/widows 裁决（`src/Hutu/Rendering/Layout/Fragmentation/ChildSlicer.cs:95`）。
- `FragmentSlicer.Fitting`：行级 orphans/widows 裁决（`src/Hutu/Rendering/Layout/Fragmentation/FragmentSlicer.cs:44`）。

### 3.3 多栏落地

`BlockLayout` 的多栏路径已实现：

- 直接子块跨栏、强制断点、balance/auto、`column-wrap` 行盒、spanner 分节（`src/Hutu/Rendering/Layout/BlockLayout.cs:839`、`:993`、`:1900`、`:2842`、`:2952`）；
- 可断行块按行切片，`box-decoration-break: slice/clone` 的边框、padding、背景（`FragmentSlice`，`ChildSlicer.cs:78`；`ArrangeMulticolRun` 的 `:3007`）；
- 全浮动容器的列打包、浮动段落的第一列 band（`:1006`、`:1805`）；
- 表格内部写下的**强制**断点传播到表格边界（`:943`）。

`InlineLayout.LayoutColumns` 把行分到各栏并应用 orphans/widows（`src/Hutu/Rendering/Layout/InlineLayout.cs:1117`）。

### 3.4 分片绘制入口

`LayoutEngine.Arrange` 已经接受 `LineRange`（取哪几行）与 `FragmentSlice`（这一片拥有哪些边、背景按整盒裁剪），`src/Hutu/Rendering/Layout/LayoutEngine.cs:1234`。页面分片可以直接复用这两个参数，不需要重造一套"切开的盒子"。

## 4. 缺口清单

### 4.1 页模型与 `@page`（P0）

**4.1.1 没有页面序列 / 页盒。** 布局入口是单个根盒 + 视口：`LayoutEngine.Layout(root, viewportWidth, viewportHeight)`（`src/Hutu/Rendering/Layout/LayoutEngine.cs:108`）；`LayoutResult` 只有文档宽高（`src/Hutu/Rendering/Layout/LayoutResult.cs:52`）；`RenderingPipeline` 只有一份 `Layout`/`Paint`（`src/Hutu/Rendering/RenderingPipeline.cs:65`、`:71`）。没有"页数、每页内容、页盒（margin box / page area）"这一层。

**4.1.2 `@page` 规则从未进入引擎。** AngleSharp 能解析出 `ICssPageRule`（`deps/AngleSharp.Css/src/AngleSharp.Css/Dom/Internal/Rules/CssPageRule.cs:24`），但它只有 `SelectorText` 和 `Style`；`RuleBucketIndex.Build` 只收集 style / counter-style 规则（`src/Hutu/Rendering/Boxes/RuleBucketIndex.cs:395`），`GetMatchingStyles` 只产出 Media/Supports/Style（`deps/AngleSharp.Css/src/AngleSharp.Css/Extensions/StyleSheetExtensions.cs:34`）。`@page` 的声明既不进元素 cascade，也没有页面 cascade。`@page` 内的 margin box 规则（`@top-center` 等）在解析器里只 `FillDeclarations`，`ICssMarginRule` 在 deps 里没有任何实现（`deps/AngleSharp.Css/src/AngleSharp.Css/Dom/ICssMarginRule.cs:7`）。

**4.1.3 没有打印媒体。** `HutuRenderDevice.Category` 恒为 `DeviceCategory.Screen`（`src/Hutu/Rendering/HutuRenderDevice.cs:182`）。`@media print` 的媒体查询按 `DeviceCategory.Printer` 判定（`deps/AngleSharp.Css/src/AngleSharp.Css/Extensions/MediaListExtensions.cs:50`），所以永远不匹配；`<link media>` / `<style media>` 这层也没有按设备求值（`RuleBucketIndex.SheetApplies`，`:1300`）。AngleSharp 本身已有 `Printer` 类别，把设备做成可配置是低成本的，但当前没有入口。

**4.1.4 页面属性一条都没注册。** `size`、`page-orientation`、`page`（命名页）、`marks`、`bleed` 既不在 AngleSharp 的 `PropertyNames` 里，也不在 Hutu 的原始值白名单 `RawValueNames` 里（`src/Hutu/Rendering/WideKeywordDeclarationFactory.cs:50`）。按该文件自己的注释，未注册的声明在解析期就会被丢掉。因此：

- 页面尺寸关键字（`a4`、`a5 landscape`、`jis-b5`…）和显式宽高没有来源；
- `@page { width/height }` 的 over-constrained 语义没有；
- `page-orientation: rotate-left/rotate-right` 没有；
- `margin: inherit` 等页上下文继承没有（`@page` 的页面上下文按规范继承自根元素）；
- 命名页 `page: foo` 没有。

**4.1.5 页上下文（background/border/visibility/继承）没有。** 页面自身的背景、边框、padding、`visibility` 与根元素继承的页上下文都没有落点。语料里 `page-name-table-001` 要求 `@page square { size: 5in; background: #eee }`，`root-element-display-none-print` 要求根 `display:none` 时页面空白，`page-margin-006` 要求 `@page { margin: inherit }` 从根元素继承 0.5in。

**4.1.6 页选择器 / 命名页 / 分页组没有。** `:first`、`:left`、`:right`、`:blank`、`:nth()` 的页面选择器没有；命名页的传播规则（block/flex/grid/table/inline-block/orthogonal/abspos/display:none）、页面组、命名页变化处的强制分页都没有。今天 `wpt/css-page` 的 58 对里有 **45 对**用了 `page:` 属性。

**4.1.7 页码 / 页边盒 / 运行页眉没有。** `CounterEnv` 只有元素计数器（`src/Hutu/Rendering/Boxes/CounterEnv.cs:22`），未知计数器返回 0（`:177`），所以 `content: counter(page)` 现在会静默画成 0。`counter(pages)`、`string-set`、`position: running()` + `content: element()`、脚注都没有。

### 4.2 页级断点语义（P0/P1）

**4.2.1 普通流里的 `break-*-page` 完全无效。** `ColumnsOf` 对非多栏盒直接返回 false（`src/Hutu/Rendering/Layout/BlockLayout.cs:844`），而所有 `Breaks.*` 消费点都在多栏路径（`BlockLayout.cs:945`、`:1194`、`:1248`、`:1298`；`InlineLayout.cs:536`、`:1132`）。所以在普通块里 `break-before: page`、`page-break-before: always` 什么都不做——屏幕模式这是对的，打印/分页没有消费者。

**4.2.2 多栏里 page/column 不分。** `Fragmentainer.Forced` 把 `Page/Column/Left/Right` 一律当强制断点（`Fragmentainer.cs:255`），`ChildSlicer.Forces` 同样（`ChildSlicer.cs:149`）。在多栏里这大概率符合"页断点降级为栏断点"，但无法表达"这是页断点，不该在栏里消费"。

**4.2.3 左右页 / 空白页 / recto-verso 没有。** `Left/Right` 在样式里保留，但没有任何页面奇偶、页进方向、空白页插入的实现；`recto/verso` 被直接映射成 `Right/Left`（`BreakStyle.cs:99`），丢掉了竖排/书写模式下的含义。

**4.2.4 `Left/Right` 的入口还不一致（现存小 bug）。** `HoldsForcedBreak`（`BlockLayout.cs:920`）和 `IsForcedBreakCarrier`（`:1222`）只检查 `Column or Page`，而 `Fragmentainer.Forced` / `ChildSlicer.Forces` 还认 `Left/Right`。结果：`columns:1` 的 fragmentainer 里只写 `break-before: left` 时，容器根本不会走多栏路径，断点被丢掉；`columns:2` 时又会被当成强制栏断点。分页实现需要统一这组谓词。

**4.2.5 断点传播不完整。** CSS Break 3 §3.1.1 要求强制断点沿祖先传播到最近的 fragmentainer 边界。当前：

- `Fragmentainer.Before/After` 两个组合助手已经写好（`Fragmentainer.cs:229`、`:234`），但**全仓没有调用点**（只有 `Combine` 被表格路径用）；
- `BoundaryBreaks` 只对 `BoxKind.Table` 读内部强制断点（`BlockLayout.cs:943`）；
- `EffectiveMembers` 只拆"仅承载强制断点的裸 wrapper"，而且只拆一层（`BlockLayout.cs:1142`、`:1182`）。

因此 `break-before: page` 写在带 border/padding 的 section 的第一个子元素上、或写在裸 wrapper 里再套一层时，断点会消失在整块盒子里。

**4.2.6 `avoid-page` / `avoid-column` 不分。** 两者都变成 `Avoid`（`BreakStyle.cs:97`、`:111`）。分页里 `avoid-column` 不该阻止页断，`avoid-page` 才该；现在会过度避免。

**4.2.7 `break-inside: avoid` 只对"可断行块"有意义。** `BreaksAtLines` 只在块没有块级子元素时才允许按行切（`BlockLayout.cs:1240`），有块级子元素的盒子本来就整块放置，所以 `break-inside: avoid` 对它既不需要也不生效。分页需要块级子元素的 avoid（CSS2/pagination 的 `block-page-break-inside-avoid-*` 正是这个）。

### 4.3 可分片内容（P0/P1）

**4.3.1 嵌套块不切（最大的结构性缺口）。** `FragmentsInColumns(child) => !HasBlockChildren(child) && BreaksAtLines(child)`（`BlockLayout.cs:1232`）；源码注释自己写着有块级子元素时"breaks inside those first, which is the next step of the fragmentainer"（`:1120`）。但这一步没实现：任何 wrapper 一有块级子元素就整块放置。真实文档（body > section > div > p）几乎全部命中。

**4.3.2 表格不切。** `TableFormattingContext` 不读 `Breaks.*`，也不接 fragmentainer；只有"表格内部强制断点传播到表格边缘"（`BlockLayout.cs:943`）。缺：行/行组切片、跨页边框与背景、重复 `thead`/`tfoot`、caption 只在首片。今天 `css-break/table` 34 个失败；CSS2/pagination 的 row/rowgroup/table 共 18 对也依赖它。

**4.3.3 flex/grid 不切。** `FlexFormattingContext` / `GridFormattingContext` 里没有任何 Break/fragment 引用（`Algorithm.cs:20` 的 "breaks" 指 flex 换行）。flex/grid 容器作为子块时必是 `HasBlockChildren`，整块放置。今天 `css-break/flexbox` 26 个失败、`grid` 2 个。

**4.3.4 浮动不能跨片。** `ColumnsOf` 遇到"浮动 + 流内容"混合直接退回普通堆叠（`BlockLayout.cs:888`）；全浮动容器才按列打包，浮动段落有一条专门路径（`:1006`、`:1805`）。浮动本身没有切分，`float-with-large-margin-bottom-cross-page` 失败；CSS2/pagination 的 `float-page-break-inside-avoid-*` 7 对也依赖浮动跨页。

**4.3.5 竖排不分页。** `ColumnsOf` 对 `box.Flow.IsVertical` 直接 false（`:844`）；`InlineLayout` 的多栏测量也要求 `!flow.IsVertical`（`InlineLayout.cs:529`）。竖排文档没有逐列/逐页分片。

**4.3.6 越界子元素直接禁用多栏。** 任何 out-of-flow 子元素都令 `ColumnsOf` 返回 false（`BlockLayout.cs:859`）。这意味着一个带绝对定位饰件的多栏盒会整体退回普通堆叠；分页需要把 abspos/fixed 按"静态位置所在页"挂到页上，而不是让整块失去分片。

**4.3.7 monolithic 规则本身是对的，但页面没有承接。** `BreaksAtLines` 把定高、min/max-height、裁剪、size containment、替换元素、正交书写都视为整块（`:1240`），这与规范一致；页面需要的是"整块放不下时溢出当前页并继续下一页"的顶层规则，现在没有。

**4.3.8 分片装饰只做了背景/边框的 slice。** `FragmentSlice` 支持边框、padding、整盒背景（`ChildSlicer.cs:78`），但 `css-break` 顶层的 `borders-001..008`（8 个）、`box-shadow-002..005`（4 个）、`box-decoration-break-clone-009`/`clone-split-inline`（2 个）、`transform-006/008`（2 个）仍失败。分页会继承这些缺口：断点处不该画的阴影/边框、每片重复的 clone、跨片的 transform 几何。

**4.3.9 inline / trim / block-in-inline 的耦合。**

- 行级分片已有 `LineRange` 与 inline ancestor 的 per-fragment 盒子（`InlineLayout.cs:4037`），可复用；
- `text-box-trim` 目前按"每一栏的首/末行"裁剪（`InlineLayout.cs:1161`、`:1725`），3 个 `not-at-page-edges` 失败；页边缘是否裁剪是"实现自定"，但至少要先有页面才能决定；
- `block-in-inline` 拆分仍是独立架构缺口（`docs/css2.1-reftest-progress_zh_CN.md` §7），`inline-page-break-inside-avoid-1` 同时受它阻塞。

### 4.4 绘制、宿主与 API（P0）

- **单页绘制 API。** `RenderingPipeline` 只有一份 `Layout`/`Paint`（`RenderingPipeline.cs:65`、`:71`）；`PaintList` 只有 `Commands` 与 `FixedCommands`（`src/Hutu/Rendering/Paint/PaintList.cs:28`、`:34`），没有页分组、页背景、页序列。宿主拿到的是"一整张文档"，不是"若干页"。
- **固定定位只画一次。** `position: fixed` 锚到视口（`src/Hutu/Rendering/Layout/Position/PositionedLayout.cs:143`），`FixedCommands` 只在执行器里画一遍。分页需要每页重复（`page-name-fixed-pos-001`），且页边盒/固定元素要相对 page area。
- **abspos 没有"页"的归属。** 单个文档坐标系里 abspos 只有一个包含块；分页需要按静态位置/包含块判断它落在哪一页，跨页的 monolithic 溢出（`monolithic-overflow-027/028`）还要特殊处理。
- **页背景/边框/页边盒没有绘制路径。** 画布背景目前按根盒/body 传播（`Painter` 的页面级处理），分页后要按页面积绘制，并叠加 `@page` 的背景/边框。
- **`print-color-adjust` 未读。** AngleSharp 有该属性（`PropertyNames.PrintColorAdjust`），Hutu 没有任何读取点；打印默认的 background 经济模式会与 Chrome 不一致（12 个 css-page 测试显式写 `print-color-adjust: exact`）。
- **阅读器翻页也没有。** Avalonia 等宿主是滚动视图（`HutuSurface` 的 desired size 是文档尺寸，executor 接受 scroll），没有"按视口切页"的宿主 API。若目标只是阅读器翻页，也需要新 API；但它不是 CSS 分页，不能替代 `@page`。

### 4.5 验证与语料（P0）

- `ReftestRunner` 固定 800×600 单张位图（`src/Hutu.Conformance.Tests/ReftestRunner.cs:47`、`:64`），`EngineReader` 只设视口（`:48`），没有打印媒体、页面尺寸、页面序列比较。
- 结果就是 §2.2 的假通过：`css-page` 58/58、`CSS2/pagination` 41/41 全部，以及 `gecko/pagination` 的 73 个通过中的大多数，都不携带分页信息。
- 需要一个新的打印 runner：以 `Printer` 设备加载、按 `@page` 尺寸分页、逐页栅格化、比较页序列与页数（包括 `match`/`mismatch` 的页数语义），否则修完分页也无法用这套语料判分。
- 单测层面零覆盖：`src/Hutu.Tests` 里没有 `@page`、页面尺寸、页选择器、命名页、页码、页边盒的测试；只有 `BreakStyleTests` 读值。

## 5. 缺口 → 语料对照（可用来排期）

| 缺口 | 直接相关语料 | 当前状态 |
|---|---|---|
| 页序列 + `@page` 尺寸/边距/背景 | `css-page` 的 `page-size-*`、`page-margin-*`、`layers-001`、`page-visibility-hidden`、`root-element-display-none` | 58/58 假通过 |
| 命名页传播 + 页选择器 + 左右页 | `css-page` 的 `page-name-*`（45 对用 `page:`）、`page-orientation-*` | 假通过 |
| 嵌套块分片 + 断点传播 + avoid | `CSS2/pagination` block 15 对、部分 float/inline；`css-break` 顶层 51 | 未实现 |
| 表格分片 + 重复表头/表尾 | `css-break/table` 34、`CSS2/pagination` row/rowgroup/table 18 | 未实现 |
| flex/grid 分片 | `css-break/flexbox` 26、`grid` 2 | 未实现 |
| 浮动跨页 | `CSS2/pagination` float 7、`css-break/float-with-large-margin...` | 未实现 |
| 固定/abspos 按页 | `page-name-fixed-pos`、`page-name-abspos-*`、`monolithic-overflow-*` | 未实现 |
| 分片装饰/阴影/transform | `css-break/borders-*`、`box-shadow-*`、`transform-*`、`box-decoration-break-clone-*` | 多栏里仍失败 |
| 打印媒体 + 打印验证 | 全部 `-print*` 162 对 | 屏幕模式，不可判 |

## 6. 建议落地顺序

### M1：页模型骨架（P0，先让"页"存在）

目标：能按 `@page` 生成页面序列，并逐页绘制。

1. 设备可配置为 `Printer`；`@media print` / 媒体查询按打印设备求值；`media` 属性按设备判断。
2. 收集 `@page` 规则（含 `@layer` 内的），实现页面 cascade：命名页、`:first/:left/:right/:blank`（可先只做默认页）；读取 `size`（关键字/长度/横竖）、`margin`（含百分比按页尺寸、`inherit`）、`background`/`border`/`padding`/`visibility`、`width`/`height` 的 over-constrained 语义。
3. 生成页面序列：page area 作为每页的初始包含块；根盒按页 extent 分片；强制 `break-*-page` 生效；页数由内容决定；`left/right/recto/verso` 与空白页先按 LTR 首页为右页实现。
4. 逐页绘制：页背景/边框、内容裁剪、`position: fixed` 每页重复、abspos 静态位置分页、`print-color-adjust`。
5. 打印 runner：按 `@page` 尺寸逐页出图，比较页序列/页数；把 `-print*` 语料从"屏幕单页"迁到打印模式。
6. 单测：`@page` cascade、size/margin 百分比、页面序列、强制分页、固定元素每页、页背景/边框。

验收：`basic-pagination-001/005`、`page-size-011/013/014`、`page-margin-005/006`、`layers-001`、`page-visibility-hidden-001`、`root-element-display-none-print` 在打印 runner 下有意义地通过；新增单测先红后绿。

### M2：递归分片 + 表格（P0/P1，真实文档能跨页）

1. 递归分片 API：放不下的块要能"把自己交给下一片"，不再要求 `!HasBlockChildren`；把 `Fragmentainer.Before/After` 接上，强制断点沿祖先传播；`break-inside: avoid` 在块级子元素上生效。
2. 分片边界的 margin 规则：断点处截断、片内相邻 margin 正常折叠（当前多栏路径是逐项相加，需先对照 Chrome 测量）。
3. 表格分片：行/行组切片、跨页边框/背景、重复 `thead`/`tfoot`、caption 只在首片；`break-inside: avoid` 在 row/rowgroup/table 上生效。
4. 分片装饰收尾：borders、box-shadow、box-decoration-break clone、transform 在断点处按规范绘制。

验收：`CSS2/pagination` 的 block/table/rowgroup/float/inline 组；`css-break/table` 34、顶层 `borders/box-shadow/widows` 组明显下降；新增嵌套块、表格行、avoid 的单测。

### M3：命名页 + 页选择器 + 页码/页边盒（P1，拿 css-page）

1. `page` 属性注册（raw value）+ 传播规则（block/flex/grid/table/inline-block/orthogonal/display:none/abspos/fixed）。
2. 页面组、命名页变化处强制分页、`:first/:left/:right/:blank`、`page-orientation`。
3. 页边盒（`@top-center` 等 16 个）与生成内容；`counter(page)` / `counter(pages)` 接入页面环境；之后再做 `string-set` / `running()`。
4. `@page` 在 `@layer` 中的 cascade 顺序。

验收：`css-page` 58 对在打印 runner 下从假通过变为真通过（或暴露新缺口）；页选择器/页边盒/页码单测。

### M4：剩余内容类型（P2）

flex/grid 分片（26+2）、浮动与页浮动、竖排分页、fixed/abspos 的完整页归属、`print-color-adjust`、脚注、`marks/bleed`。这一层与 CSS Break 3 / CSS Page 3 的完整度有关，部分语料没有覆盖，按产品需要排。

## 6.5 验收对照（M1–M4 逐项，2026-10-30）

口径：✅ 已实现并有单测或打印语料佐证；◆ 按用户决定记为后续里程碑（需要"按页重排"层）；— Chrome 未实现或语料口径不接受，故不实现（附实测依据）。

| 项 | 状态 | 佐证 |
|---|---|---|
| M1.1 Printer 设备 / `@media print` | ✅ | `HutuRenderDevice.Category`、`EngineReader.UsePrintMedia` |
| M1.2 `@page` 收集与 cascade（size/margin/area/frame/选择器/命名页/层叠） | ✅ | `PageStyleTable`、`PageStyleTests`（22 个） |
| M1.3 页面序列（page area 作 ICB、强制断点、左右页/空白页） | ✅ | `PageSequenceTests`；`css-page`、`CSS2/pagination` 打印对全绿 |
| M1.4 逐页绘制（页框、裁剪、fixed 每页、abspos 页归属） | ✅ | `PagePaintTests`（固定盒每页、页号边距）；abspos 页名单测 |
| M1.5 打印 runner + `-print*` 语料 | ✅ | `ReftestRunner.DrawPages/ComparePages`；185/198（含 `cdd35675` 转入的 34 对） |
| M2.1 递归分片、强制断点沿祖先传播、块级 avoid | ✅ | 嵌套块/行/表格行/行组单测；CSS2 avoid 组打印对全绿 |
| M2.2 断点处 margin 截断 | ✅ | 强制断点取盒边框顶，页首不留 margin |
| M2.3 表格分片、caption 只在首片、row/rowgroup/table avoid | ✅ | 表格组打印对全绿；caption 单测 |
| M2.3 重复 `thead`/`tfoot` | ✅ | 续片画表头（`e7aa3963`）与表脚（`d21eeb97`）：`PageSequenceTests` 各 1 个、`PagePaintTests` 逐页像素各 1 个；`row-page-break-inside-avoid-2-print` 等表格打印对未回退 |
| M2.4 分片装饰（`box-decoration-break` 等） | ✅（能力） | `4f73067a`：clone 盒子的分片边带（页起/页止落在盒内时补出被切的边，占用高度还给流），第一片跑到盒内首个断点（巨型分片，`031` 保持绿）；单测 `ARepeatingBoxPutsItsEdgeOnThePageItIsCutIn` |
| M2.4 的 `032`（tentative） | ◆ | 实测：机制已对（页 3 = 290+10、第 3 页起补边），但页数 7 vs 9——ref 的流自带盒子边界（30/20 行短页），且同一对被切的两个 monolithic 子盒一个不分、一个分；该文件自注"the expected behavior here is not obvious"，复刻 Blink 怪癖不在规范要求内 |
| M3.1 `page` 属性传播（block/flex/grid/table/inline-block/正交/abspos） | ✅ | 命名页、正交流单测；`page-name*` 打印对全绿 |
| M3.2 命名页变化强制分页、`:first/:left/:right/:blank`、`page-orientation` | ✅ | `PageStyleTests`/`PageSequenceTests`（含空白页） |
| M3.3 页边盒 16 个、`counter(page/pages)` | ✅ | `PageMarginBoxes`；页号绘制、空白页计数测试 |
| M3.3 `string-set`/`running()` | — | Chrome 未实现，无对照 |
| M3.4 `@page` 的 `@layer` 顺序 | ✅ | 后层 > 前层、无层 > 层 的层叠单测 |
| M4 flex/grid 分片 | ✅ | flex 行/列、grid 行单测；flex/grid 打印对全绿 |
| M4 浮动（整页/clear） | ✅ | 浮动与 `<br clear>` 打印对全绿 |
| M4 页浮动（`float: top` 等） | — | Chrome 未实现 |
| M4 竖排分页 | ✅ | `PageAxis`；竖排切页与逐页绘制测试 |
| M4 fixed/abspos 页归属 | ✅ | 固定盒每页绘制测试 |
| M4 `print-color-adjust` | — | 实测：语料的 aqua 以背景画在 test、以边框画在 ref，按 `economy` 丢背景会破坏 4 对 float-clear（已实测回滚）；引擎按 `exact` 绘制，与语料口径一致 |
| M4 脚注、`marks/bleed` | — | Chrome 未实现，无对照 |

## 7. 关键代码位置索引

| 主题 | 位置 |
|---|---|
| 断点样式读取（含 legacy） | `src/Hutu/Rendering/Layout/Fragmentation/BreakStyle.cs:77` |
| avoid-page/avoid-column 合并 | `src/Hutu/Rendering/Layout/Fragmentation/BreakStyle.cs:97`、`:111` |
| 分片枚举与 Forced 判定 | `src/Hutu/Rendering/Layout/Fragmentation/BreakStyle.cs:7`；`Fragmentainer.cs:255` |
| 未接线的边界组合助手 | `src/Hutu/Rendering/Layout/Fragmentation/Fragmentainer.cs:229`、`:234` |
| 子块切片（forced/avoid/orphans/widows） | `src/Hutu/Rendering/Layout/Fragmentation/ChildSlicer.cs:95`、`:167` |
| 行切片（orphans/widows） | `src/Hutu/Rendering/Layout/Fragmentation/FragmentSlicer.cs:44` |
| 多栏入口与门槛 | `src/Hutu/Rendering/Layout/BlockLayout.cs:839`、`:844`、`:859`、`:888`、`:897` |
| 表格内部强制断点外传 | `src/Hutu/Rendering/Layout/BlockLayout.cs:943` |
| 裸 wrapper 拆解 / 强制断点载体 | `src/Hutu/Rendering/Layout/BlockLayout.cs:1142`、`:1182` |
| 可切分块的条件（无块级子元素） | `src/Hutu/Rendering/Layout/BlockLayout.cs:1232`、`:1240` |
| 多栏子块/行/节测量 | `src/Hutu/Rendering/Layout/BlockLayout.cs:993`、`:1264`、`:1900` |
| 多栏排布与 FragmentSlice | `src/Hutu/Rendering/Layout/BlockLayout.cs:2842`、`:2952`（`:3007` 起） |
| 行分栏 + 每栏 trim | `src/Hutu/Rendering/Layout/InlineLayout.cs:1117`、`:1161`、`:1725` |
| 页面感知的 Arrange 入口 | `src/Hutu/Rendering/Layout/LayoutEngine.cs:1234` |
| 单页布局入口/结果 | `src/Hutu/Rendering/Layout/LayoutEngine.cs:108`；`LayoutResult.cs:52` |
| 单页渲染管线 | `src/Hutu/Rendering/RenderingPipeline.cs:65`、`:71` |
| 绘制列表（无页分组） | `src/Hutu/Rendering/Paint/PaintList.cs:28`、`:34` |
| fixed 锚到视口 | `src/Hutu/Rendering/Layout/Position/PositionedLayout.cs:143` |
| 设备恒为屏幕 | `src/Hutu/Rendering/HutuRenderDevice.cs:182` |
| 只收集 style/counter-style 规则 | `src/Hutu/Rendering/Boxes/RuleBucketIndex.cs:395` |
| 样式表 media 不按设备求值 | `src/Hutu/Rendering/Boxes/RuleBucketIndex.cs:1300` |
| AngleSharp 的 @page AST（只有 SelectorText + Style） | `deps/AngleSharp.Css/src/AngleSharp.Css/Dom/Internal/Rules/CssPageRule.cs:24` |
| @page 只 FillDeclarations（无 margin box） | `deps/AngleSharp.Css/src/AngleSharp.Css/Parser/CssBuilder.cs:349` |
| ICssMarginRule 无实现 | `deps/AngleSharp.Css/src/AngleSharp.Css/Dom/ICssMarginRule.cs:7` |
| 媒体类型 → Printer | `deps/AngleSharp.Css/src/AngleSharp.Css/Extensions/MediaListExtensions.cs:50` |
| 原始值白名单（不含 page/size/...） | `src/Hutu/Rendering/WideKeywordDeclarationFactory.cs:50` |
| 元素计数器（无 page/pages） | `src/Hutu/Rendering/Boxes/CounterEnv.cs:22`、`:177` |
| 打印 reftest 的固定窗口 | `src/Hutu.Conformance.Tests/ReftestRunner.cs:47`、`:64` |
| 测试用的引擎加载（只设视口） | `src/Hutu.Conformance.Tests/EngineReader.cs:48` |
| 假通过清单的已知盲区 | `conformance/vacuous-passes_zh_CN.md`（§"这套检测抓不到什么"） |
| 表格跨 fragmentainer 的既有结论 | `docs/css2.1-reftest-progress_zh_CN.md` §7、§11 |

## 8. 待验证 / 待拍板

1. **分片边界的 margin 折叠。** `ArrangeMulticolRun` 逐项加 `Margin.Top/Bottom`（`BlockLayout.cs:2981`、`:3056`），而普通块堆叠走 `MarginGroup` 折叠。CSS Break 3 只规定断点处 margin 截断，片内相邻 margin 仍应折叠。需要先对 Chrome 测量再改，别把多栏现有的边距行为当规范。
2. **`Fragmentainer.Balance` 的 orphans/widows。** 源码注释已承认平衡搜索没有追问"这个切法是否被块的 orphans/widows 允许"（`Fragmentainer.cs:180` 附近）。分页本身不走 balance，但嵌套 `column-fill: balance` 会继承这个限制。
3. **最后一页的进度保证。** `Fill` 对最后一个 fragment 有 "至少吃一个子块" 的兜底（`Fragmentainer.cs:30`），`FillLines` 只有 `spills: true` 的 auto-fill 才兜底。页面是动态无限的，"放不下也必须前进"的规则要在页循环里显式保证，否则可能死循环或丢行。
4. **页边缘 `text-box-trim` 的策略。** 语料自己注明"page edges 怎么处理取决于实现"；当前实现按每栏首/末行裁剪，做页面前要定策略。
5. **页边盒 / 运行页眉 / 脚注的范围。** 这些不在当前导入语料里（WPT 的 css-page 覆盖不到），但 PDF/打印产品通常需要。若第一版不做，要在 API/里程碑里明确"页边盒为空"。
6. **左右页的首页方向。** LTR 横排首页通常是右页；竖排/RTL 的页进方向、`recto/verso` 映射要写进设计，不要沿用 `recto -> Right` 的简化。
7. **`SheetApplies` 的 media 语义。** 代码只判断 media 文本为空（`RuleBucketIndex.cs:1300`），注释却说非空也纳入；两者矛盾。做打印媒体前要先把这层的行为测清楚。
8. **目标输出形态。** 是"浏览器式打印预览/打印"，还是"PeachPDF 式 PDF 出页"？两者对页边盒、页码、字体嵌入、分页器 API 的要求不同，会直接影响 M1 的 API 设计。

---

## 7. 目标验收清单（逐项 → 证据 → 状态）

目标：按本报告顺序推进分页 M1–M4，逐切片实现、测试、全量门禁并提交。

| 要求 | 证据 | 状态 |
|---|---|---|
| M1 打印媒体 / `@page` / 页面序列 / 逐页绘制 / runner | §6.5 各行 + `PageStyleTests`(22)/`PageSequenceTests`/`PagePaintTests`；打印 runner 输出 `build/reftest-print-score.md` | 完成 |
| M2 递归分片 / 表格分片 / thead-tfoot / avoid / 超高块填页 / 裁剪不延长文档 | 对应单测 + `CSS2/pagination`、`css-page` 打印对；`0fad0a00`、`c334961d`、`21b1a580` | 完成（M2.4 除外） |
| M3 命名页 / 选择器 / 页码 / 页边盒 / `@layer` | `PageStyleTests` 22 个；命名页与页边盒打印对 | 完成 |
| M4 flex/grid/浮动/竖排/fixed-abspos | flex/grid 行、竖排切页、固定盒每页单测；对应打印对全绿 | 完成 |
| 全量门禁（每片） | 单元 2164 全过；打印 189/198；屏幕 2113（起于 2122，0 新增） | 持续满足 |
| **M2.4 `box-decoration-break: clone`** | `031` 已绿；能力已落地（`4f73067a` + 单测）；`032` 实测**页数 7 vs 9**（不进入像素比较），为 tentative 怪癖 | **能力完成**；`032` 记为已知差异：实测分解：test 版式 1820 行（10+1800+10），ref 1920 行——差 100 = ref 六个盒子各带 10+10 边框共 120，但 test 只有 20：即 **5 个分片边界 × 20 行（每边界补一组上下边框）**。ref 的切点也随之落在流断点上（870/900/1220/1500），不是页边倍数；而且有的页要画多段（页 4 = 黑10+青10+灰10 三段）。所以做法是：在合法断点处给文档插入 20 行边框带，再把页切成"文档段列表"，断点与切点互为因果需迭代到不动点。**已实做过半步并量过**：把 clone 盒子的上下边带按"页起/页止落在盒内"补上（`Lead`/`Trail` + `Repeats`），结果 `031`（原本绿）从 5 页变 6 页——因为 031 期望的正是页边**不**补边的巨型分片；`032` 页数仍 7 vs 9。已整片回滚。结论：边带必须与"巨型分片"同时实现。**两步都已落地**（`4f73067a`）：收集器记录 clone 盒子与其上下边带；构建循环在"页起/页止落在盒内、且已过该盒第一个断点"处补边（第一片跑到盒内第一个断点，页边不补边——巨型分片）。结果 `031` 保持绿、打印仍 189/198；`032` 的机制对了（第 3 页起补边、页 3 = 290+10）但页数仍 7 vs 9——ref 的流里有它自己的盒子边界（30/20 行的短页），我们的单盒流没有，这一条只能作为"规范能力已具备、tentative 用例不复刻 Blink 怪癖"记录 |
| （超范围）fieldset legend | 24 对 21 绿；剩 3 个 grid 对，落点见 §6 注记 | 未完成（模块级） |
| （超范围）多栏跨页 | `column-balancing-paged-001`：`2f1f669f` 后转绿（按节断点 + 单列多栏下钻） | 完成 |

**收口（2026-10-30，用户裁定）**：M1–M4 全部按"能力"验收完成。M2.4 以 `4f73067a` 的分片边带 + 巨型第一片为准（有单测），`monolithic-overflow-032` 记为**已知差异**——该 tentative 用例要求复刻 Blink"第一个分片横跨多页、其后页边才补边"的怪癖，同一对被切的两个 monolithic 子盒一个不分一个分，其自注也写明 "the expected behavior here is not obvious"，不属规范要求。非分页特性（`@scope`×3、`(scripting)`、表单控件外观、`mask-image`）与 `001-grid`（reftest 两侧对 br 断点语义冲突）不计入 M1–M4。

---

*本报告只做调查；未修改引擎代码。所有代码位置均以 HEAD `7357d65a` 为准。*

---

## 9. css-break 语料清理（屏幕口径）

目标：逐切片修复 `wpt/css-break` 在屏幕与打印两个口径下的失败对。每片门禁 = 单元测试全过 + 打印 189/198 + 屏幕全量 **0 新增失败**（按对名比对，不看总数）。

### 9.1 切片 A：栏切过"没有断点可断"的盒子

实测：屏幕全量 **2108 → 2100**（转绿 8，新增 0）；`wpt/css-break` **104 → 98**；打印仍 **189/198**；单测 **2164 → 2169**（新增 `MulticolBoxCutTests` 5 个）。
转绿：`css-break/borders-001..004`、`css-break/fieldset-002`、`css-break/out-of-flow-in-multicolumn-014`、`gecko/columns/column-balancing-overflow-000`、`wpt/css-gaps/multicol/multicol-gap-decorations-040`。

| 位置 | 做了什么 |
|---|---|
| `BlockLayout.cs:1573` `AddBoxCut` | 空盒子（无子、非替换、写明 height/min-height）比整栏高时按栏高切片；每片用 `FragmentSlice` 只画自己拥有的上下边与背景（CSS Break 3 §4.7），`MulticolChild.Offset` 记住这片在整盒里的起点 |
| `BlockLayout.cs:1320` / `:1339` `SpliceOverset` / `IsOversetWrapper` | 栏里**最后一个**成员如果是"什么都不画、且写的比自己装的高"的 wrapper（无 border/padding/margin/背景/outline/box-shadow/clone/裁剪/contain，宽 auto、高写明），把它打开，让内容直接进栏被切 |
| `BlockLayout.cs:1427` `FillsTo` | 填栏容器的切点 = 它写明的栏高，或它最终达到的高度——`max-height` 夹住结果时，那也就是栏高（`multicol-fill-auto-block-children-003`） |
| `LayoutContext.cs:268` `WithFragmentainer`（+ `BlockLayout.cs:124`、`:624`、`:1445`、`:3245`） | 标记"已经站在别人的栏里"；嵌套多栏里不切，因为外层还不能把内层拆开摆放它的碎片 |
| `FlexFormattingContext.cs:167` + `LayoutEngine.cs:1451` | flex 容器也接 `FragmentSlice`：空的定高 flex 容器被切时，边与背景与块盒一致（`gecko/flexbox/pagination/flexbox-empty-1a..1h`） |

**Chrome 实测基准**（`build/_frag_probe.html`，hit-test 探针，非截图）：3×100px 的 `column-fill: auto` 栏里，250px 空盒 → 100/100/50 三片；外面套一层 `height:2px` 的盒 → 结果完全相同（证明"打开 wrapper"是对的）；150px → 100/50；`height:48px` wrapper 里 `height:800%` 的子盒 → 三栏都被填满（overflow 的内容确实跟着分片，不是一整块垂在栏外）。

四条"先不切"的范围线，每条都有实测的对立语料：

1. **片数 > 栏数不切**：`column-fill: auto` 的溢出列还没画，切了会把尾巴丢掉——`out-of-flow-in-multicolumn-019` 的 ref 侧要 5 片而内层只有 2 栏，实测丢 400px 绿。
2. **盒子有内容不切**：内容会在每片从头重排（重复画字），需要的是"按片裁内容"。`contain-size-breaks-001` 就是这条：ref 侧空盒靠 `padding-top` 得到高度被切、test 侧 `contain:size` 的盒有文字不切，两侧不再一致；加上"写明 height/min-height 才切"后恢复绿。
3. **嵌套多栏不切**：`multicol-rule-nested-balancing-003`（外层被 `ColumnsOf` 主动退回普通堆叠，`BlockLayout.cs:943`，所以内层的片没人摆）。
4. **balance 容器不切**：去掉 `column-fill: auto` 门槛实测 `break-inside-avoid-min-block-size-1` 由绿转红（只有写明高度的一侧被切），故保留。

下一步（同一族的解锁顺序）：按片裁内容 + 分片装饰（clone/阴影/transform）→ `borders-005..008`、`box-shadow-002..005`、`box-decoration-break-*`；补 `column-fill: auto` 的溢出列 → 解除第 1 条；外层切内层多栏 → 解除第 3 条；table/flex/grid 分片仍是独立里程碑。

### 9.2 已做过并回滚的实验：断点处的 box-shadow

按 CSS Break 3 §4.7 实现过一版：切片盒子的阴影改用**整盒**的 shape（`LayoutBox.BackgroundOrigin`），再用一条 band 夹住——被切开的边不放阴影带，盒子真正拥有的边才放到 `bounds` 那么远；band 沿切断轴取片段自身范围，沿另两轴取整盒阴影的可达范围（`BoxShadowCommand` 加 `Band`，`SkiaPaintExecutor.DrawBoxShadow` 外层 `Save()+ClipRect`）。
实测：`box-shadow-002` 11926 → **9282** px，`003/004/005` **没变**。因为没有一对翻绿，增益无法用门禁证明，整片回滚。

残差不在阴影上。`box-shadow-002..005` 的 **ref 页是 abspos 盒子**，靠 `inset-inline-start` / `inset-block-start` 摆放三条边带；我们渲染 ref 时三条全落在父盒原点（paint 实证：`border 58,83.72 70x110`、`border 58,83.72 70x100`、`border 58,83.72 70x60`，x/y 完全相同）。全仓 `src/Hutu` 里 `inset-inline` / `inset-block` **零读取点**，而 fork 已经注册了这些属性（`deps/AngleSharp.Css/src/AngleSharp.Css/Factories/DefaultDeclarationFactory.cs:3032`），所以这是纯 Hutu 侧缺口。语料面：**223** 个 reftest 文件用了逻辑 inset，`css-break` 族内 **13** 个。

下一片（不依赖分片）：把四个逻辑 inset 按 writing-mode × direction 折到物理边（CSS Logical 1 §12.2），物理属性已声明时保留其值，`inset` / `inset-block` / `inset-inline` 简写展开；单测覆盖 8 种模式组合，然后重测 `box-shadow-002..005`、`borders-005..008`、`out-of-flow-in-multicolumn-*`。阴影 band 那条改动等这一片能把 `box-shadow-*` 送到可判分的距离后再重新落地。

### 9.3 切片 B：逻辑 inset + 断点处的阴影带

实测：屏幕全量 **2100 → 2098**（新增 0）；单测 2169 → **2178**（`LogicalInsetTests` 6 个、`ShadowAtTheCutTests` 3 个）；打印 189/198 不变。

| 位置 | 做了什么 |
|---|---|
| `PositionStyle.cs:163` `Fold`（`Read` 与 `ReadFused` 两条读路径都走它） | 四个逻辑 inset 按 writing-mode × direction 折到物理边：水平方向 block-start=top、水平 LTR inline-start=left；vertical-rl 的 block-start=right、vertical-lr 为 left；竖排的 inline 轴随 `direction: rtl` 反向。物理边已有值时保留（`auto` 与"没写"分不开，按已写处理）。 |
| `FusedStyleFactory.cs:1153` | 把本盒的 `writingMode` / `direction` 交给 `ReadFused`。 |
| `PaintCommand.cs` `BoxShadowCommand.Band` + `Painter.cs` `BandOf` + `SkiaPaintExecutor.DrawBoxShadow` | 分片盒子改画**整盒**的阴影（`BackgroundOrigin`），再用 band 夹住：被切断的两边止于切点，盒子真有的边放到阴影可达范围；后端 `Save()+ClipRect` 实现。`box-decoration-break: clone` 的分片两边都是真边，band 自然为整段。 |

`box-shadow` 四对的变化（像素差）：`002` 11926 → **412**、`005` 12626 → **996**、`004` 9642 → **8137**、`003` 6198 → **5442**。都还没翻绿：`002` 的残差是整像素边框与抗锯齿阴影带之间 1px 的软边（规范上等价），`003` 是 vertical-rl 的 band 路径（单测覆盖了映射，但这对的残差还没定位），`004/005` 待查（005 只差 996）。另有两对顺带转绿：`css-shapes/shape-outside-circle-054/055`（ref 用逻辑 inset 摆圆）。

`inset` / `inset-block` / `inset-inline` 三个简写由 AngleSharp 解析成长手，Hutu 只读长手，所以简写自动覆盖。剩余缺口：物理与逻辑同时写时以"物理已写"为准，没有按层叠顺序判定（两者落在同一个物理属性上，规范由层叠决定）。

### 9.4 切片 C：圆角按整盒量，而不是按分片量

`LayoutEngine.ResolveDecorations` 原来用**分片**的 border box 解析 `border-radius`；CSS Backgrounds 3 §5.1 的等比缩小会因此把"盒子真有角的那一片"的半径缩小（`borders-005`：整盒 290 高、半径 80，最后一片只有 70 高 → 半径被缩到 35）。现在切片盒子用 `BackgroundOrigin`（整盒）解析，切断边照旧清零四角（§4.7）。

实测：屏幕全量 **2098 → 2097（新增 0）**；`borders-005` 转绿；单测 2178 全过；打印 189/198 不变。

### 9.5 切片 D：clone 的盒子按内容切，每片自己再补边

`AddBoxCut` 原来对 `box-decoration-break: clone` 的盒子也按整盒 border box 切，结果最后一片只有剩余高度（`borders-008`：content 240 + 上下各 10 的边，三栏 100 高 → 100/100/60，末片半径还被缩到 30）。clone 的语义是**每片都自带边**（CSS Break 3 §4.7），所以切点要落在内容上：每栏放 `栏高 − insets` 的内容，每片高度 = 内容片 + insets。改后 240 的内容按 80/80/80 分开，每片 100 高，与 ref 的三个圆一致。

实测：屏幕全量 **2097 → 2096（新增 0）**；`borders-008` 转绿；单测 2179（`MulticolBoxCutTests` 加 1 个 clone 用例）；打印 189/198 不变。

### 9.6 切片 E：被 max-height 夹住的 wrapper 也打开

`IsOversetWrapper` 只认"写明了 height"的 wrapper；`fieldset-007` 的 wrapper 用 `max-height:100px` 夹住自己，内容 300px 溢出，Chrome 一样顺着栏分片。现在"写明 height"或"写了 max-height"都算给盒子定了高（内容是否真的越界由后文的 `held > size.Height` 判定）。

实测：屏幕全量 **2096 → 2095（新增 0）**；`fieldset-007` 转绿；单测 2180（`MulticolBoxCutTests` 加 1 个 max-height 用例）；打印 189/198 不变。

### 9.7 量过但回滚的三件事（同一块缺口的三个侧面）

**空白子节点算不算内容。** `box-decoration-break-clone-009` 的盒子只有卷起来的空白文本子节点，`AddBoxCut` 的 `Children.Count > 0` 因此不肯切它。把"只有可折叠空白"视为空（`HoldsAnything`）后 `clone-009` 与 `gecko/pagination/abspos-breaking-001` 都转绿，但 `wpt/CSS2/floats-clear/floats-clear-multicol-001` 变红：它的 ref 页那个空 `.container` 因此被切（正确），test 页的 `.container` 里装的是两个 float，我们的引擎还不能把 block 里的 float 按栏分片，两侧于是发散。1 红换 2 绿不合门禁，整片回滚。

**OOF 与平衡（`out-of-flow-in-multicolumn-094/095/096`）。** 三对的 ref 都要求整块绿（多栏容器高 200 而不是平衡出来的 100）。Chrome 实测（`build/_oof_balance.html`）：两个 100px 子盒 + 一个 `.rel` 包着 abs(100) → 容器 **200**；abs 直接作容器子盒 → **100**；对照 → 100。即"abspos 的包含块落在多栏内部的普通流盒里"时 Chrome 让片段器撑到那个盒的静态位置，而包含块是多栏本身时 abspos 按栏分片、不撑高。规范里没有对应条款，记为 Chrome 怪癖，不实现。

**`transform-006/008`。** 变换盒自己 175 高、子盒 300 高，ref 要求**每一片**各自算 `transform-origin` 且内容跟着裁。我们的盒有内容就不切（切了内容会在每片重排），所以这一对与 `borders-006/007`（竖排多栏）、`break-inside-avoid-min-block-size-2`、`table/repeated-section/text-in-nested-multicol` 卡在同一处。

**下一片（最大解锁）：按片裁内容。** 切片盒子的子盒按**整盒**排一次，每片只画属于自己那一段（`BackgroundOrigin` 已有整盒矩形，渲染时再加一条裁到分片的 clip），不再按片重排。它同时解锁 `transform-006/008`、`break-inside-avoid-min-block-size-2`、`table/repeated-section/text-in-nested-multicol`、`out-of-flow` 的一部分，以及 `widows-orphans-021/022` 这类"float/abs 在分片块里"的用例。再往后是竖排多栏（`ColumnsOf` 现在对 `box.Flow.IsVertical` 直接返回 false），解锁 `borders-006/007`、`box-shadow-003/004`。
### 9.12 家族测绘：flexbox 与 table 的剩余缺口形状

- **flexbox（22 对）**不是单一机制：最小的 10 对同签名（`58,50`、白 vs 绿、10000 px）分两种——`single-line-column-flex-fragmentation-026/070a-d/071` 是"`overflow:clip` + `max-height` 的 flex 容器高过栏、内容要跨栏继续"；`multi-line-row-flex-fragmentation-083a-d` 是"item 的 `break-before` 传播到 flex line"。另一半（`059..061`、`065..071`、`093..096`）是 item 尺寸/多行行盒的细节。
- **table（18 对）**分两组：4 个小的 repeated-section 对（`balanced-inner-multicol` 700、`text-in-nested-multicol` 224、`variable-fragmentainer-size-001/002`）需要"栏里的表格重复区段"；14 个 paint 对（`table-*-paint-*`、`collapsed-borders-paint-*`、`table-border-007/008`、`caption-and-cells`）是"表格部件被栏切开时按片绘制背景/边框"，一次可覆盖一大片。


### 9.13 columns 遇到 out-of-flow 子盒：让分栏成形（差一对，已回滚）

追到根因：`ColumnsOf` 的"任何 out-of-flow 子元素 → return false"（§4.3.6 的旧范围线）让 `single-line-column-flex-fragmentation-026` 这类页**根本不形成分栏**（探针：`COL inline=100 count=2 used=2 kids=2` 随后在 OOF 子盒处返回 false；dump 里 flex 容器仍是整宽）。改成"OOF 子盒不算拒绝理由"（它们在盒子的 padding box 上定位，与栏无关），并在**含 spanner**时保留旧的拒绝（spanner 的 run 还没教过怎么摆这些盒子）。再叠加"裁剪盒内容切片"（`FragmentClip` + 按整盒排布 + 裁到片 + `max-height` 算有界高度），`026` **转绿**、`borders-001/005` 与 `multicol-span-all-fieldset-002/003` 保持绿、12 个探测家族 0 回归。

但全量屏幕门禁里 `gecko/bugs/379349-2a` 红 4396 px（`.fno1` 段落位移）。**关掉两处新切割许可后仍是 4396**，说明嫌疑在 `ColumnsOf` 的 OOF 改动本身，而不是内容切片；该页没有 OOF 子盒，机制未查明。整片回滚，WIP 存 `build/abs-columns-wip.cs`。下一步：在该页给 `ColumnsOf`/`HoldsSpanner` 打探针，看是哪一条路径因 OOF 改动而改变了分栏结果。

**孤立实验（已做）**：只应用 OOF 那一条改动（不动切割许可、不加 `FragmentClip`），`379349-2a` 依旧红 4396 px，`026` 依旧红（它还需要内容切片）——确认红的是 OOF 改动本身。查看该对：`379349-2a-ref.html` 的 multicol 里有 `position:absolute` 的边带（x=48/198/348，第三条落在容器 300 宽之外），改动让它**从"整体堆叠"变成"分栏"**，边带落点因此改变；test 页没有 OOF 子盒、行为不变，于是两侧发散。


### 9.14 切片 G：分栏与 out-of-flow 子盒共存 + 裁剪盒内容切片

三处改动：
1. `ColumnsOf` 不再因 out-of-flow 子盒拒绝分栏（它们在盒子的 padding box 上定位，与栏无关）；**含 spanner** 时仍拒绝，因为 spanner 的 run 还没教过怎么摆这些盒子。
2. `ArrangeMulticolChildren` **第一次真正摆放这些 OOF 子盒**（此前多栏分支把它们直接丢掉了）：用 `PositionedLayout.ForDescendants(context, box, parent.PaddingBox, parent.BorderBox)` 锚到本盒的 padding box。
3. **裁剪盒内容切片**：`overflow` 不为 visible 的盒子即使有内容也可被栏切开——切出的片按**整盒**排布（内容只排一次），每片设 `LayoutBox.FragmentClip` 为片自己的矩形，`OverflowClipEdge` 认它，于是每片只画自己那一段；`max-height` 与 `height` 一样算"有界高度"。

实测：屏幕全量 **2093 → 2091（新增 0）**；转绿 `css-break/flexbox/single-line-column-flex-fragmentation-026`、`gecko/abs-pos/multi-column-1`、`gecko/bugs/379349-2a`；`borders-001/005`、`multicol-span-all-fieldset-002/003` 保持绿；单测 2180；打印 189/198 不变。


### 9.15 试过"栏内子盒持有的 OOF 续栏"（未翻绿，已回滚）

在 `ArrangeMulticolRun` 排好一个栏内子盒后，把它持有的 out-of-flow 子盒按栏带切开：可见部分设 `FragmentClip`，余下部分把**包含块连带平移**（`+pitch` 进下一栏、`-boxHeight` 上移）再排一次、只取该 OOF 子盒加进树。实现完整、`026/379349/borders-001` 保持绿，但 `out-of-flow-in-multicolumn-001/004` 残差**一点没动**（840/1600 px）。

dump 001 后看清真正缺口：`.rel`（高 20，栏剩 10）在我们这里是**被推到第 2 栏**（fill 语义），Chrome 则把它**切开**（10 在栏 1 底、10 在栏 2 顶）——abs 的静态位置随之分片。这是"并行流"模型：只要含有定位后代，块就不该整块下推。下一步应在 `Fragmentainer.Fill`/`MulticolChild` 上体现这一点（含 OOF 后代的子盒允许跨栏），而不是继续在 abs 一侧打补丁。

### 9.16 切片 H：表格在栏里可以被切开（表部件按片绘制）

表格此前被 `AddBoxCut` 的 `kind is Table` 与"有块级子盒"两道门槛挡住，永远整块留在第一栏。现在表格允许被栏切开：切片沿用 789ecd13 的裁剪机制（按整盒排布 + `FragmentClip` 裁到片），并给 `TableFormattingContext.Arrange` 接上 `FragmentSlice`（与 flex 同法：边、padding、`SlicesBlockStart/End`、`BackgroundOrigin`），于是每片只画自己拥有的边框。

实测：屏幕全量 **2091 → 2090（新增 0）**；`css-break/table/table-border-007` 转绿（14000 → 0）；`table-border-008` 仍红但残差变化（36400 → 49025，属另一机制：行/列背景的按片绘制）；`table-col/row-paint` 未动（需行级断点与部件背景切片）。单测 2180；打印 189/198 不变。


### 9.17 两族同一个根因：Chrome 在栏尾**切开**带定位后代的块，我们把它整块下推

`table-row-paint-*` 的 **ref 页根本不是表格**：它画的是一个 `position:relative`、400 高的块，里面用 `position:absolute` 的色带模拟"每片的行背景"（红色顶/底 caption、两条 110/135 的橙色渐变行、一条白色竖缝），放在 4×100 的栏里。也就是说这些 ref 依赖的是：**这个 400 高的相对块被 4 个栏切开，绝对定位的后代随它的每一段落到对应栏**。

同一条缺口在 `out-of-flow-in-multicolumn-001/004` 已经量过：`.rel`（高 20、栏剩 10）我们**整块下推**到第 2 栏，Chrome 在栏尾切开（10+10），abs 的静态位置随之分片。

所以下一片（**并行流分片**）的规则是：
1. 一个块在栏里放不下 *剩余* 空间时，若它**持有 out-of-flow 后代**，就不下推，而在栏尾切开；余下部分进下一栏顶部（与 `FragmentClip` 机制一致）；
2. 第一片的可用高度是**本栏剩余**（不是整栏高），其后每片为整栏高；
3. 切开只影响绘制与后代定位，块的 in-flow 内容仍按整盒排一次。

预期解锁：`out-of-flow-in-multicolumn-001/004/045/073/090`、`table-row-paint-*`、`table-col-paint-*`（ref 同形），以及 `table/repeated-section` 的一部分。试过的两个近似实现（直接子盒跨栏续排、按行分带）都没翻绿，已回滚；差别正是"按剩余空间切"与"按整栏高分带"。=> 表格这一族的下一步：`FragmentSlice` 的边只影响"画"、不应影响"排"——目前按整盒排布时把切边为零使内容盒变大 10px（007 无行内容所以没暴露）；行/列/区段的背景与折叠边框需要按片裁切（`table-*-paint-*` 14 对）。=> 下一步沿用同一机制：OOF 盒子**跨过栏边界时按栏分片**（379349 的 ref 第三条边带落在容器外，仍按整盒摆放；`out-of-flow-in-multicolumn` 9 对需真正的逐栏分片），以及同源的 `single-line-column-flex-fragmentation-070a-d/071`。

=> 这一族的真正缺口是**栏内 abspos 跨栏分片**（CSS Position 3 §5.5：OOF 后代由片段器分片，跨过栏边界的那一段在下一栏顶部继续）。026 的 abs 正好落在第一栏内（不需要分片），而 379349 的 ref 需要。下一步实现它：在 `ArrangeMulticolChildren` 之后，把包含块落在本盒流内的 OOF 盒子按其量出的矩形与各栏求交，逐栏摆放（平移到该栏 x、裁到该栏）——这与 `out-of-flow-in-multicolumn` 9 对同源。
**试过"裁剪盒的内容切片"（未提交）**：给 `LayoutBox` 加 `FragmentClip`、让 `OverflowClipEdge` 认它、`AddBoxCut` 放行 `overflow: clip` 的盒子、切出的片按**整盒**排布再裁到片。`borders-001/005` 保持绿，但 `single-line-column-flex-fragmentation-026` 一点没动——探针显示该页 `AddBoxCut` **根本没被调用**：dump 里 flex 容器仍是 100 宽（= 容器宽），说明 `ColumnsOf` 在这些页没走分栏路径。下一步先查 `ColumnsOf` 为何对 `columns:2; width:100px` 的子盒判否（在 `ColumnsOf` 内打一条探针），再谈内容切片。


### 9.18 表格按栏切开，片的整个画面裁到片

上一节留下的第一层是"片里的内容没人裁"：切出的片按**整盒**排布（`whole` rect + `FragmentClip`），但绘制侧只有 `OverflowClipEdge` 认 `FragmentClip`，而它只在 `ClipsOverflow` 打开时才被推到命令表里；`PaintGround`/`PaintInk`（块级背景、边框、文字的真实绘制路径，`PaintBox` 只走层叠上下文根和替换盒）根本不看 `FragmentClip`。所以 4 片会把整张 400 高的表在每栏原样再画一遍。

这一片三处：

1. `HoldsPositioned`：持有 out-of-flow 后代的块，只要有声明/最小/最大高，就可被 `AddBoxCut` 切开（横排；见下）；
2. **auto 高的表格**可切（表格是片段器该切的盒子），但持有定位后代时仍不切——`table/repeated-section/abspos-uncontained-text` 的 abs 锚在视口上，Chrome 只画一次，切口不该把它裁掉；
3. 绘制：`FragmentClip` 进 `CutOf`（`PaintBox` 的层叠上下文根），并在 `PaintGround`/`PaintInk` 里把这一片的整个画面（背景、边框、文字、整棵子树）裁到片矩形。

实测（基准 `e214ebd4`）：全量屏幕 **gecko 606 不变、wpt 1484 → 1483**；`wpt/css-break` 91 → **90**，`overflow-clip-012` 转绿，新增失败 **0**。表格族像素大幅下降但未翻绿：`sections-and-captions-mixed-order` 30205 → **851**、`specified-block-size-007` 21126 → **2475**、`table-border-008` 49025 → **13600**、`table-collapsed-borders-paint-at-boundary` 34100 → **10105**、`collapsed-htb` 21960 → **9130**、`grid-paint-htb` 62800 → **17600**。竖排各对保持基准——`ArrangeMulticolRunAcross` 还没把切片的块向边映射对，`HoldsPositioned` 与表格豁免都只在横排生效（竖排不切，行为不变）。单测 2180；打印 189/198。

剩余（下一片）：`row/col/section/grid-paint-htb` 的残差是**表格部件背景的几何**，不是分片——行背景目前铺满整行、把 `border-spacing` 的 30px 空档也画上（`row-paint-htb` 恰好 **7350 = 30 × 245**），`col` 背景则根本没有盒子可画。下一步按"单元格并集"绘制行/区段/列背景。`out-of-flow-in-multicolumn-001/004` 仍需上一节的"按剩余空间切 + reach"（块的 reach 要算到 OOF 后代，`AddBoxCut` 目前只看盒子自己的块向尺寸）。


### 9.19 行/行组的背景画在单元格下面，不铺 border-spacing

上一节量出的 7350 px 就是它：行的背景铺满整行，把 `border-spacing` 的 30px 空档也画上（30 × 245 = 7350）。CSS 2.1 §17.5.1：行/行组的背景只在其单元格后面，单元格之间那圈间距属于表格，保持空。做法是 `PaintPresentation` 对 `TableRow`/`TableRowGroup`（分离边框模型）按后代单元格矩形逐个 push clip、同一份背景各画一次——渐变仍以部件自己的盒子为锚，所以在空档两侧连续。单元格与行列背景的绘制顺序不变（表格 ground → 行的 ground → 单元格）。

实测：`table-row-paint-htb-ltr` 7350 → **0** 转绿、`table-section-paint-htb-ltr` 8850 → **0** 转绿，另有 `css-flexbox/flexbox_stf-table-row-group` 转绿；竖排同族下降未绿（`row-paint-vlr/vrl` 32030 → 29930、`section-paint-vlr/vrl` 33240 → 30630）。全量屏幕 **gecko 606、wpt 1483 → 1480，新增失败 0**；单测 2180；打印 189/198。

下一步：`col` 的背景（`TableBuild.ColumnBoxes` 有盒子、没有绘制；按列 × 行的单元格带画，`col-paint`/`grid-paint` 同时需要），以及 `collapsed-borders-paint-*` 的切边边框拼接。

### 9.20 列背景试过、量过，被 RTL 表格的列镜像挡住（未提交）

`col`/`colgroup` 的背景在我们的绘制里没有路径：它们不是布局树里的盒子（`TableBuild.ColumnBoxes` 有它们，但没有 LayoutBox）。做出来的版本（已回滚）：

1. 表格布局期把每列的**带**算出来存到表格 LayoutBox 上：`Area` = 该列从首行顶到最后一行底的整条（排除表格边缘的 `border-spacing`），`Rows` = 每行的裁剪带；渐变以整条为锚、逐行裁剪，60° 这类斜向渐变不会逐行重新起相；
2. `CollectColumnHints` 按 `col` 的 `span` 属性把列盒/宽度提示展开成多份（`<col span=2>` 管两列，HTML §4.9.12）；
3. 绘制期在表格自己的背景之后、边框之前，逐列逐行带 push clip 后画该列背景（CSS 2.1 §17.5.2：列背景在表格背景之上、行与单元格之下）。

实测：`wpt/css-break/table/table-col-paint-htb-ltr` 17150 → **0**（转绿），`table-col-paint-vrl-rtl` 30630 → 5280。但全量比较出现一对新失败：`wpt/css-tables/paint/col-paint-htb-rtl`——它此前是**空转通过**（两边都不画背景，页面几乎空白），一旦 ref 侧的列背景可见就暴露真正的缺口：**我们的表格 grid 一律从左往右排列，RTL 表格的首列没有镜像到右边**。ref 页正是不镜像的 LTR 表（宽 50 在前），所以 test 页（宽 100 在前）与它对不上。竖排的 `col-paint-vlr/vrl` 也还差（30630），根子同样是表格自身的竖排/RTL 映射。

结论：列背景要落在"RTL（及竖排）表格列镜像"之后，作为独立一片先做网格镜像（`ColumnX`/列宽/单元格落点/折叠边框一起翻），再把上面三步接回来；本轮未提交、已回滚，工作树停在 `d4079812`。

### 9.21 按剩余空间切持有 OOF 的块：001 转绿

§9.17 的并行流分片落地了。机制：`MulticolChild.Divides`（`column-fill:auto`、横排、块向 margin 为零、持有 out-of-flow 后代）→ `PlanMulticolRun` 逐栏给出 piece（整块或切块；余下部分从下一栏顶续排，`NeededCells` 按块高给足栏数）→ `ArrangeMulticolRun` 消费 piece：切块按**整盒**排一次再 `FragmentClip` 到片（与表格切片同一套）。两个关键细节：①`ChildSlicer.Fitting` 对**第一个**装不下的子盒会返回 1（保证流程前进），切块必须在它之前拦截，否则持有 OOF 的首子永远整块溢出；②续排完最后一片时要把 `placed` 推到该子之后，否则它会被再整块排一次。

三处限定让它不连坐：表格不参与（表格有自己的切片片）；`context.InFragmentainer` 为真的子盒不切（019：嵌套多栏的 `.rel` 归内层栏管）；`MulticolRules` 的"已填充列"只对**带 `FragmentClip` 的片**用新判据（片真正占的那段与栏带相交），整块子盒仍走原来的近似判定——`wpt/css-gaps/multicol` 的 78 对一条不动。顺带 `001` 的 16px 白色 `column-rule` 因为切块而露出来，也一并修好。

实测：`out-of-flow-in-multicolumn-001` 840 → **0** 转绿。全量屏幕 6 对转绿（001、`overflow-clip-012`、`table-row-paint-htb-ltr`、`table-section-paint-htb-ltr`、`flexbox_stf-table-row-group`、`text-transform-full-size-kana-007`），新增失败 0——`gecko/text/476378-soft-hyphen-fallback` 在整量里偶现，单跑三遍皆绿，属已知 flaky。单测 2180；打印 189/198；`wpt/css-break` 88 → **87**。

剩余（下一片）：`reach`——切分长度目前只看盒子自己的块向高度，`045/004/073/090` 需要算到 OOF 后代的底（045 的包装盒 200 高、004/073/090 的盒子只有 0 高但 abs 伸出 200~400）。

### 9.22 续排的片要落在下一栏，不是下一行（记一笔）

接 §9.21：`045` 的续排片虽然分出来了，却落在第一栏的**下方**而不是第二栏——`ArrangeMulticolRun` 的非换行分支拿 `counts.Count`（普通填充的栏数）当列数，而切块计划可能比它多一格，于是 `row = cell / columns` 把第二片算成了第 2 行（y +100）。把列数改成 `Math.Max(counts.Count, cells.Count)` 后，045 的第二片正确落到 `(-42,-50.28)`（第二栏顶、`y-offset` 的整盒位置），但该对的 10000 px 没动（它还要 `reach` 和更细的 abs 几何），而 `093` 的像素反而从 28 涨到 269，按"不翻绿不动"的规则回滚了这一行。下一片做 `reach` 时把这一行一起带上，并先看 093 为什么会因此偏 1px。

### 9.23 `reach` 量出来了，但没有翻绿：还差"片数不能超过栏数"

接 §9.21/§9.22 补 `reach`：切块的长度不再只看盒子自己的块向高度，而是 `PositionedReach`——直接 out-of-flow 子盒的 `top`（`auto` 时按它前面 in-flow 内容的累计高当静态位置）加它量出的高度，`fixed` 不计。量到的效果：`073/090` 11600 → **10000**（`.rel` 真的按 0/100/200 分三段了），`004/045` 不动。

但没有一对翻绿，而且 `073` 的第三片落到了第 3 栏（`columns = max(counts, cells)` 时），多栏容器只画两栏，那片就画到盒子外面去了。Chrome 的 ref 只要 100×100 的绿方块：abs 跨两栏各 100，第三段不该有。所以下一片还要一条和 `AddBoxCut` 一样的门槛——**切出来的片数不得超过栏数**（`ceil(reach / columnHeight) <= columns`），超了就整块放着让它溢出（§4.3），再配"续排片落在下一栏"的列数（§9.22 那一行）。这轮 `reach`、列数两处都回滚，工作树停在 `5281cf6b`。

### 9.24 `break-before: avoid` 把切口推进裸包装里

`break-between-avoid-003`：一列里 4 个 50 高的方块，最后一个写着 `break-before: avoid`，栏高 160。Chrome 把切口退到第 2 块之后（100+100），我们把 3 块塞进第一栏（150+50）——因为 `avoid` 落在**裸包装**与最后一个方块的边界上，而 `EffectiveMembers` 只在"包装的直接子盒里有强制栏断"时才拆包装，`avoid` 不算。按 CSS Break 3 §4.1，裸包装（无边框/内边距/外边距/背景/声明尺寸）本就可以当作页面直接写在栏容器里。

改法：`IsForcedBreakCarrier` 拆出 `IsBareWrapper`（只做"裸"的判定），`EffectiveMembers` 在"下一个成员写着 `break-before: avoid`"时也把裸包装打开，且 `FlattenBare` 递归展开（一层不够：`<div><div><div>` 里那两层都要打开，`avoid` 才能退到第 2 块后）。`avoid` 到的边界由 `ChildSlicer.Fitting` 原有的回退逻辑处理。

实测：`break-between-avoid-003` 5000 → **0** 转绿；全量屏幕 7 对绿（另有 `overflow-clip-012`、`table-row-paint-htb-ltr`、`table-section-paint-htb-ltr`、`flexbox_stf-table-row-group`、`text-transform-full-size-kana-007`），新增失败 0（`gecko/text/476378-soft-hyphen-fallback` 整量偶现、单跑两遍皆绿，仍是已知 flaky）；单测 2180；打印 189/198；`wpt/css-break` 87 → **86**。

### 9.25 续排的片落回下一栏：`abspos-in-opacity-001` 转绿

§9.22 记的那一行终于带对了上下文：`ArrangeMulticolRun` 的非换行分支用 `Math.Max(counts.Count, cells.Count)` 作列数。§9.22 时单独改它翻了 093 的 1px 又没翻绿，所以回滚；现在切块已经能分片（§9.21），续排片必须落到**下一栏**而不是下一行。`abspos-in-opacity-001`（`position:relative` > `opacity:.5` > 200 高 abs，切成两片各 100）第一片的 abs 落在栏 1、第二片落在栏 1 的下方——列数一改，第二片到栏 2 顶，蓝底上的 50% 红正好铺成 100×100 的紫色方块。`abspos-in-opacity-002/003` 也从 16000/13000 降到 8000/9000（未绿）。

实测：`abspos-in-opacity-001` 10000 → **0** 转绿；全量屏幕 8 对绿、新增失败 0（`gecko/bugs/1608124-1` 是字体回退页，整量偶现、单跑两遍皆绿，仍是 flaky）；单测 2180；打印 189/198；`wpt/css-break` 86 → **85**。§9.22/§9.23 里那行从此落地。

### 9.26 记一笔：整族跑与单跑会给出不同的 001（跨页状态泄漏）

量 `abspos-in-opacity-001` 时发现：`HUTU_SCORE_ONLY=wpt/css-break/abspos-in-opacity-001` 单跑是绿的，`=wpt/css-break/abspos-in-opacity`（连同 002/003）跑就是 10000 px 红——同一份二进制、同一渲染器。用 `LooksAtOnePairClosely` 单独画这一对时两张图字节相同。也就是说 harness 在**同一进程里连续画多页**时有状态残留（字体/度量缓存之类），这解释了此前 `gecko/text/476378-soft-hyphen-fallback`、`gecko/bugs/1608124-1` 这类"整量偶现、单跑皆绿"的 flaky。门禁判据仍然只看**整量**跑的名字差集，但以后量单页时要知道单跑结果不能当作反证。

同轮还量过：`reach`（§9.23）+ `FitsTheColumns` 门槛 + 续排落下一栏，`abspos-in-opacity-002/003` 从 16000/13000 降到 8000/9000（`001` 保持绿），但没有新翻绿；再把"整行为空的行盒当作无可断点"接进 `MulticolChildren` 后 002/003 又回到 10000。按"不翻绿不动"整片回滚，工作树停在 `c383f789`。

### 9.27 竖排 flex 容器当作可切的成员列表，强制栏断递归找

flexbox 子族的第一片。`single-line-column-flex-fragmentation-070a-d` 的形状是"栏容器 > 竖排 flex > 两个竖排 flex（第二个/内部某项带 `break-before|after: column`）"，Chrome 在 flex 项之间断栏，我们因为"有子盒的盒子不切"（`AddBoxCut` 的 children 门槛）整块塞进第一栏。

做法：`IsBareWrapper` 接受 **竖排、不换行、无装饰无声明尺寸** 的 flex 容器（`flex-direction: column` 的项沿块轴堆叠，和块的子盒一样是块轴上的成员；行向容器不算）。`IsForcedBreakCarrier` 的判定改为 `ForcedBreakWithin`——递归穿过这些裸包装找强制栏断，所以 `break-before: column` 写在**内层 flex 的某一项**上也能把外层打开（070b/070d 就是这种）。打开后由 `EffectiveMembers` 把项变成栏容器的成员，`break` 值由 `ChildSlicer` 照常处理。

实测：070a/b/c/d 四对 10000 → **0** 全绿；全量屏幕 12 对绿、**新增失败 0**（gecko 606、wpt 1484 → 1483+… 里含此四对）；单测 2180；打印 189/198；`wpt/css-break` 85 → **81**。仍未绿的 `071` 需要 flex 项自身的跨栏分片（160 高的项 > 栏高），`multi-line-*` 一族需要行向 flex 的跨栏排版。

### 9.28 行向 flex 的"整行是成员"：083a-d 四对转绿

`multi-line-row-flex-fragmentation-083a-d` 是 070 的行向版本：栏容器 > 行向 wrap flex > 两个行向 flex，强制栏断落在第二个 flex 上（083a/c）或落在它/前一个 flex 的**首/尾项**上（083b/d）。

做法三处：①`IsBareWrapper` 再接受一种 flex——行向、wrap、**每个子盒都是 flex 容器**（此时一个子盒就是一行，行与行之间才由块轴共享）；②`BoundaryBreaks` 让 flex 的**首项 `break-before` / 尾项 `break-after`** 的强制值传播到容器边界（CSS Flexbox 1 §6：项上的强制断先变成 flex 换行）；③`ForcedBreakWithin` 也看 flex 子盒的首/尾项，好让外层被打开。

实测：083a/b/c/d 四对 10000 → **0** 全绿；全量屏幕 16 对绿、新增失败 0（`gecko/text/476378-soft-hyphen-fallback` 仍是已知 flaky，单跑两遍皆绿）；单测 2180；打印 189/198；`wpt/css-break` 81 → **77**。

剩下的 flexbox：`single-line-column-flex-fragmentation-071`（flex 项自身比栏高，需要项内跨栏分片）、`multi-line-row-flex-fragmentation-065/067/068/069/070/071/093-096`（多行行向、项内分片）、`multi-line-column-059-061`（列向 wrap + space-between 的段间间隙）、`flex-container-fragmentation-006`、`single-line-row-044/050`。

### 9.29 flex 项跨栏切片：绕开 `AddBoxCut` 会打断 borders 一族（未提交）

`single-line-column-flex-fragmentation-071` 需要"flex 项比栏高时在项内切开"：项是 `height:120; padding:20` 的块，栏高 100，第一栏还剩 60——Chrome 把项切成 60+100。我们的填法把项整块溢出（`Fitting` 的保进度规则）。

试的版本：把这种"无可断内容、有声明高的盒"从 `AddBoxCut` 手里拿过来，交给 `PlanMulticolRun` 的切块路径（第一片取本栏剩余、余下进下一栏），判断条件照抄 `AddBoxCut` 的门槛。`071` 没动（10000），而 `wpt/css-break/borders` 一族 **0 → 8 全红**：`AddBoxCut` 的分片几何（`clonesDecoration` 的内缩、切边为零的 border/padding、`Sliced` 圆角、`BackgroundOrigin`）是绕过不了的一整套，换个路径就全丢。整片回滚，工作树停在 `00c2492b`。

结论：flex 项的跨栏切片要**在 `AddBoxCut` 内部**做"第一片 = 本栏剩余"，并让填充端把剩余空间告诉它（§9.17/§9.22 的 plan 需要携带每栏已用量），而不是另起一条切块路径。那一步是 flexbox 多行一族（065-071/093-096）的共同前置。

### 9.30 块被"推走"、flex 项被"切开"：同一形状两套行为

按 §9.29 在 `PlanMulticolRun` 里给 `AddBoxCut` 的片也开了"按剩余空间切"（`DividesHere = Divides || Offset >= 0`，续排带盒内绝对 offset，`Whole` 用 `child.Whole`）。`071` 依旧 10000，而 `borders` 一族 **0 → 6**：`borders-001` 的 120 高边框盒在还剩 5 的栏里，Chrome 是**整块推到第 2 栏**（ref 明说上边框在第 2 栏），不是切 5；`071` 的 160 高 flex 项在还剩 60 的栏里，Chrome 却是**切 60**。两者都是"无可断内容 + 声明尺寸"的盒，差别只在来源：一个是栏容器的块级子盒，一个是 flex 容器的项。

所以"按剩余空间切"不能只看盒形，还要知道它是谁断的：flex 项的跨栏切片要在 `FlexFormattingContext` 把项交给栏容器时带上这个语义（项属于 flex 容器，容器才决定在哪里断），而不能由 `AddBoxCut` 或 plan 一概而论。已回滚（`borders` 恢复 0），工作树停在 `5a51e480`。

### 9.31 整量失败集里有"单跑即绿"的幻影：先认清再动手

按 §9.26 的线索抽查了几对：`forced-break-at-fragmentainer-start-000`、`monolithic-content-with-forced-break-002/003`、`relpos-inline` 用 `HUTU_SCORE_ONLY` 指到单对时**全绿**（`LooksAtOnePairClosely` 的图也一致），但它们在整量里挂着红。`ReftestRunner.Draw` 每页都新开线程、新引擎，泄漏只可能来自进程级静态状态（字体/度量缓存一类），而 001 那对已量到"先画 002/003 再画 001 就红"。

也就是说整量失败集里有一部分是这个泄漏造成的幻影，改它们会白改。下一步（优先级高于继续翻绿）：先把泄漏钉住——用二分前缀找出"哪个前驱页会把 001 弄红"，再定位静态缓存；修掉之后基线会变，门禁判据也要按新基线重记。在那之前，凡整量红、单跑绿的条目先当幻影看待。

### 9.32 幻影的根源：画家的空剪切表是**静态共享**的

§9.26/§9.31 的"整量红、单跑绿"查到了：`Painter` 里 `NoCuts`/`NoClips` 是两个 `static readonly List`，`CutsHere()`/`CopiesOf()` 在"没有剪切"时直接把这两个**共享空表**交给 `DeferredBox`。`PaintDeferred` 又把 `entry.Cuts` 赋回 `_cuts`，而 `PaintBox`/`Collect` 会对 `_cuts` 做 `Add`/`RemoveAt`——于是某页的剪切被 `Add` 进共享表后，会出现在**并发渲染的另一页**的延迟盒子上；即使单线程，只要有一个 Add 没有配对的 RemoveAt（异常路径），也会跨页残留。harness 用 `Parallel.ForEach`（`MaxDegreeOfParallelism = ProcessorCount/2`）跑整量，所以 `abspos-in-opacity-001` 单跑绿、整量红，`gecko/text` 那几对偶现。

修法：这两处"没有剪切"改成返回**新空表**（`[]`），静态表删掉。实测：`DOTNET_PROCESSOR_COUNT=1` 与默认并发跑 `abspos-in-opacity` 三遍，结果一致（001 绿、002/003 红）；全量屏幕 **2095 → 2079、0 新增失败**（16 对绿，全是本会话切片的成果）；单测 2180；打印 189/198。`wpt/css-break` 77 → **77**——这一片是把判据修准（幻影消失、结果可复现），不是翻绿；从这一片起整量结果才可信。

### 9.33 泄漏修掉后再试 reach：002/004 翻绿，014 的续排片却跑到第 2 行

并发泄漏修掉（§9.32）之后重跑 §9.23 的 reach + "整行为空的行盒不算断点"：`abspos-in-opacity-002` 16000 → **0**、`out-of-flow-in-multicolumn-004` 1600 → **0**，两对转绿；`003` 13000 → 6000。但 `out-of-flow-in-multicolumn-014`（原绿）变 45000：它的 `.rel` 高 0、reach 150、正好放进第 2 栏剩下的 150，不需要切；改动把它的 `Fragment.Height` 变成 150 且 `Divides`，随后**绿色块第二个 AddBoxCut 片的续排片**落到了 `(8,349.72)`（第 1 栏第 2 行）而不是第 2 栏顶——计划里"续排 + 后随一个可切子盒"这一格的归属算错了。

净 +1（两绿一红），按门禁回滚。下一步先给 `PlanMulticolRun` 加一条可开关的 trace（每格列数、每片的 child/offset），把 014 这种"续排片后还有子盒"的组合钉清楚，再把 reach 接回来。

### 9.34 计划每格只问一次切片器 + reach + 空行不算断点：002/004/014 一起转绿

§9.33 的错格查清了：`PlanMulticolRun` 的 `while` 在"切片器为保进度拿走了一个装不下的子盒"之后**又去问了一次** `Fitting`（限量为负），保进度规则于是把后面的子盒也拽进这一格，014 的绿色块续排片就这样落到了第 1 栏第 2 行。普通填充每格只问一次切片器，计划也照做：一次 `Fitting` 之后无条件结束本格（切块拦截与剩余空间切片在结束前处理）。

这一片同时带回 §9.33 的两件事：`PositionedReach`（OOF 后代的下沿，`fixed` 不计）与"整行为空的行盒不算断点"（`!breakable` 走普通子盒路径，可被切），外加 `FitsTheColumns`（片数不得超过栏数）。

实测：`abspos-in-opacity-002` 16000 → **0**、`out-of-flow-in-multicolumn-004` 1600 → **0**、`out-of-flow-in-multicolumn-014` 保持绿、`003` 13000 → 11000；全量屏幕 **2095 → 2078、18 对绿、新增失败 0**（`gecko/bugs/1608124-1` 字体回退页偶现，单跑绿）；单测 2180；打印 189/198。`wpt/css-break` 77 → **75**。

剩余 OOF：`045`（包装盒 200 高、按 reach 分成两片后仍差 abs 的竖向几何）、`073/090`（片数 3 > 2 栏，按 §9.15 的规则整块溢出，Chrome 的 ref 只要 100×100）、`094-096`（平衡 + OOF 的高度怪癖）。

### 9.35 表格多出来的高按行高等比分，不按行数均分

`specified-block-size-007`：`height:400` 的表格里只有 1px（黄）与 3px（青）两行，放在 4×100 的栏里。ref 是"第 1 栏黄、后 3 栏青"，即两行被拉成 **100 与 300**——多出来的 396 按行高 **1:3** 分；我们均分（201/203），第 2 栏也成了黄色。`TableGrid.StretchRows` 改为按各行当前高度等比分配多余高度（各行都没高时退回均分），有未声明高度的行也按"参与分配的那些行"等比。

实测：`specified-block-size-007` 2475 → **0** 转绿；全量屏幕 **2095 → 2077、19 对绿、新增失败 0**（`gecko/text/initial-zwj-1` 偶现、单跑绿）；单测 2180；打印 189/198；`wpt/css-break` 75 → **74**。

### 9.36 表格的高度与背景只归"表格盒"，不含 caption（`table-caption-and-cells` 待做）

`table-caption-and-cells`：`height:2em` 的表格带一个 caption，栏高 9em、前面 8em 的空盒——ref 里 caption 所在的一条（32 高）是**白的**，黑色背景只出现在 caption 之下的 32 高区域，整表 64 高。也就是说 Chrome 把表格的 `height` 和背景都算在**表格盒（网格）**上，caption 在表格包装盒里、在表格盒之外：高度 `2em` 是网格的高，caption 再加在它上面；背景不画到 caption 带。

我们两处都不对：`Measure` 里 `gridHeight = declared - captionTop - captionBottom`（把 caption 从声明的表高里扣掉，于是行被压成 0），且表的背景铺满含 caption 的整个 BorderBox。实测差 10164 px（5120 = caption 带的背景，4918 = 少掉的行带）。

修法要走结构：表格的 LayoutBox 只包网格（caption 作为包装层的兄弟），或者给"表背景矩形"一个显式覆盖（现在只有 `BackgroundOrigin` 用于切片放置，不控制裁剪）。这一对和 `table-caption-and-cells-fixed-width`（同形）要先动这个结构，再谈其余 caption 族。

### 9.37 表格族的分布：竖排与 caption 结构是两道门槛

按差距排序看了 `wpt/css-break/table` 余下的 22 对：小差距的 4 对全是 **repeated-section**（嵌套片段器里的表头/表尾重复，机制未做）；`sections-and-captions-mixed-order`（851）是**表格模型重排**（caption 按 side 分前后、header/rowgroup/footer 分组，组内顺序与文档顺序不同），差的是列边界的 1–2px 行位；`table-caption-and-cells*`（各 10164）卡在 §9.36 的"表格盒不含 caption"；其余 10 对以上全是**竖排**（vlr/vrl）的 row/col/section/grid/collapsed-borders paint。

所以表格这一族的下一步顺序：①§9.36 的表格盒结构（解锁 caption 两对）；②竖排表格的分片映射（一次解锁 ~10 对）；③repeated-section 的嵌套片段器重复；④模型重排的边角。flexbox 一族的下一片仍是"项的跨栏切片归 flex 容器"（§9.30）。

### 9.38 表格盒不含 caption：声明的表高归网格，背景/边框只画在表格盒上

按 §9.36 做结构：①`Measure` 里声明的表高不再减去 caption（`gridHeight = declared`），caption 的高加在网格之上；②`TableFormattingContext.Arrange` 记下 `TableBox`（网格 + 其边框/内边距，去掉上下 caption 带），画家在 `PaintPresentation` 里只把**表格盒的地面**（背景、内阴影、边框、边框图）裁到它；外阴影不裁（它本来就落在盒外，裁了会整块消失——`gecko/box-shadow/tableboxshadow-basic` 就是这样红过一轮，已修）。

实测：`table-caption-and-cells` 与 `table-caption-and-cells-fixed-width`（各 10164）→ **0** 双双转绿；`table-border-008` 13600 → 13100；全量屏幕 **2095 → 2075、21 对绿、新增失败 0**（`gecko/text/476378-soft-hyphen-fallback` 偶现、单跑绿）；单测 2180；打印 189/198；`wpt/css-break` 74 → **72**。

剩下 caption/结构一侧还有 `table-caption-with-block`（基线里的大差对）和模型重排的 `sections-and-captions-mixed-order`；竖排表格分片仍是最大的一块（~10 对）。

### 9.39 没有行的表格网格也要拿到声明的表高（`table-border-008` 差最后一条黑带）

`table-border-008`：`height:180` 的表格只有两个 150 高的 caption、没有行，放在 6×100 的栏里。§9.38 之后声明的表高归网格，但 `CompleteGrid` 的"余量"只伸给**行**——没有行就没人接，网格还是 0，整表 320 而不是 500。补一行：`targetHeight` 有值而 `Rows.Count == 0` 时，网格自己就是那个高。量到 `table-border-008` 13100 → **1000**，几何与 ref 的三段结构一致（表格 500 高、切成 5 片、caption 带不画背景）。

剩下的 1000 px 是一条 `x=118..367, y=90..109` 的黑带。逐行扫图后看清了：**分片的换栏位置差 10px**——ref 在 y≈90 就换到下一片，我们在 y=100 才换；ref 的首片在 x=108/208/308（第 2/3/4 栏）各 50/100/50 高，我们的五片从第 1 栏起、每片 100。也就是说 ref 里表格的**网格带**从第 2 栏的 50 处开始（上 caption 150 之后的 50），而我们把整个表格盒（caption+网格）按 100 均切。切口位置要按"表格盒"（§9.38 的 TableBox）而不是"整表"来切。这一行改动本身不翻绿（按"不翻绿不动"回滚），但结论留给下一片：按 TableBox 切 + 补切口边框归属，`table-border-008` 应即转绿。

### 9.40 竖排表格根本没按书写模式排：行永远向下堆

probe 一页 `table-row-paint-vlr-rtl` 就看清了：表格 34x100、行按 y 向下堆、caption 高 0——`TableFormattingContext` 从头到尾没有读过 `box.Flow.IsVertical`（只在 LayoutBox 上打了个 `Flow` 标签），列永远沿 x、行永远沿 y。竖排表格要沿**行内轴（y）**分列、沿**块轴（x，vertical-rl 自右向左）**堆行，折叠边框、caption 的 side 与分片方向都要跟着换。这解释了 10 个以上的 vlr/vrl paint 对为什么几乎是空白图——不是分片没接上，是排布轴错了。这是一整块结构活，留作独立切片。

另记 `widows-orphans-002` 的悬案：4 行、4 栏、两个 2 行的块，`orphans:1; widows:2`；Chrome 平衡到 2 行/栏（每块整块进一栏），我们平衡到 1 行/栏（1/1 切开）。`column-balancing-000`（一个 3 行块、3 栏、默认 2/2）Chrome 却 1/1/1 切开——两者的差别不能靠"块高 vs 栏高"解释，需要 Chrome 探针（每块的 `getClientRects`）确认它到底在什么条件下拒绝短尾。

### 9.41 Chrome 平衡探针：`<br>` 分行的块，Chrome 倾向少用栏

用 Chrome + `getBoundingClientRect` 量了两个最小例（`column-fill:balance-all`，等宽等线高）：

- 两个 2 行块（`<br>` 分行）、4 栏：Chrome 给 **2 栏 × 2 行**（每块整块进一栏），不是 4×1；
- 一个 3 行块、3 栏：Chrome 给 **2 栏（2 行 + 1 行）**，不是 3×1。

而 `column-balancing-000`（一个 3 行块、3 栏、`column-fill` 默认）ref 是 3 栏 1/1/1——差别在**自然换行 vs `<br>` 强制断**：有 `<br>` 时 Chrome 的平衡器倾向挑"少一栏、每块尽量整"的高度；自然换行的块则按最小高度铺满。`orphans/widows` 在这里都不是主因（强制断点不受它们约束，CSS Break 3 §3.3 只约束**非强制**断点）。

所以 `widows-orphans-002`（`<br>` 块）要的是"平衡时优先让块整块进栏"的启发式，而不是 widows 规则本身；实现时不要动 `KeepsRun` 的 orphans/widows 联动，否则 `column-balancing-000` 会跟着变。两个探针页与结论留档在此。

### 9.43 flex 项按剩余空间切：只对 flex 项，`071` 转绿

§9.30 的"块被推走、flex 项被切"落地了。四件套：①栏里**高于栏的裸竖排 flex** 打开成子项（`HashSet<Box> flexItems` 记下这些项）；②`CutsAsBox`（无子盒、有声明尺寸、比栏高、片数≤栏数、无 `break-inside:avoid`、无边框、无块向 margin）；③对 **flex 项** 跳过 `AddBoxCut`，交给计划的"按剩余空间切 + 续排"；④`divides` 只在这类项上置真。

关键限定是③/④只认 `flexItems`：此前全量把 `break-inside-avoid-min-block-size-1` 弄红，是因为该页的空 div 在引擎里读到的是 `Height=40、Breaks.Inside=Auto`（`break-inside: avoid` 没进计算样式），于是被当成可切盒；限定到 flex 项后该页不走这条路，恢复绿。这类"逻辑属性读取缺口"另记，不再挡这一片。

实测：`single-line-column-flex-fragmentation-071` 10000 → **0** 转绿；`borders` 全绿、`break-inside-avoid-min-block-size-1` 保持绿；全量屏幕 **2095 → 2074、22 对绿、新增失败 0**（`gecko/text/476378-soft-hyphen-fallback` 偶现、单跑绿）；单测 2180；打印 189/198；`wpt/css-break` 72 → **71**。

### 9.44 平衡容器里，持有 OOF 的**自动高**包装也把 reach 计入平衡高

`out-of-flow-in-multicolumn-094/095/096`：多栏无高（balance），内容里有 `position:relative` 的自动高包装、里面是绝对定位的色块；Chrome 让多栏的高度把这些定位内容也**算进平衡**（ref 是 200×200 的绿方块），我们只按在流内容平衡（100 高），外层的红露出 100。修法：`MulticolChildren` 里对**平衡容器**（`divides` 为假）且**自动高**（`BlockSizeIsAuto`）的 OOF 持有者，也把该子盒的 `FragmentChild` 高度取 `max(自身, PositionedReach)`——只影响平衡高，不切块。限定自动高是关键：099 的 `.rel` 有声明高 200，计入后会把它 40000 px 弄红；限定后 094/095/096 三对一起转绿、099 保持绿。

实测：094/095/096 各 20000 → **0**；全量屏幕 **2095 → 2071、25 对绿、新增失败 0**（`gecko/bugs/1608124-1` 字体页偶现、单跑绿）；单测 2180；打印 189/198；`wpt/css-break` 71 → **68**。

### 9.45 表格的“盒”是网格盒：caption/边框/背景原点 + 可切盒的就地起切

`table-grid-paint-htb-ltr` 的 ref 是三条普通 div（140 高 lime、245 高带 10px 边框的渐变、15 高 lime），test 是带两个 caption、`border: 10px` 的同尺寸表格。Chrome 对两页逐像素一致，说明表格的分片几何与“表格盒”是同一个东西——**caption 不在表格盒内**。我们过去的四处偏差正好互相掩盖，这次一起量：

1. **caption 在 wrapper 全宽处**（CSS 2.1 §17.4）：caption 的测量基准应是表格**边框盒宽**（`contentWidth + InsetsH`），摆放的 x/宽取 wrapper（`borderBox.X/Width`），顶部 caption 从 wrapper 顶（`borderBox.Y`）起，而不是从内容区（被 10px 边框缩进）。`table-caption-*` 几对因此保持绿。
2. **表格背景的原点是网格盒**：Painter 里表格背景的 `placement` 改用 `TableBox`（网格边框盒），碎片里渐变相位才与 Chrome 一致（本例 col3 相位 10 = 60 mod 50，锚点是网格盒顶而非 wrapper 顶）。
3. **表格边框画在网格盒上**：`AddBorder` 对 `LayoutKind.Table` 用 `TableBox`；同时表格的 layout 只在“整盒排布”时保留 slice 的完整边框（片会由 `FragmentClip` 裁），在“片段矩形排布”（空表格逐片排）时仍按 slice 裁边——`table-border-007`（200 高的空表格切两栏，第二片无上边框）因此保持绿。
4. **放不下的可切盒就地起切**：`AddBoxCut` 早已把超栏空盒切成 100/100/45，但 `PlanMulticolRun` 的整块快速路径把它们当整子下推，ref 页因此被推到第 3 栏、溢出到不存在的第 5 栏。现在预切分片也走规划循环：本栏剩余 ≥ 盒的**块首边框+内边距**时，就地取一段并续排；不足则下推（Chrome 实测：5px 剩余 + 10px 边框 → 下推，0 边框 → 就地填，边框 2px → 填，padding 10 → 推）。`break-inside: avoid` 的盒子例外：放不下整块就下推，到新栏仍放不下才切（`break-inside-avoid-min-block-size-1` 实测，`FragmentChild.Inside` 现在携带真实值）。
5. 续排的片用**整盒高度**（`child.Whole`）摆位，末片才带走底边框（`portion.Last && child.Last`）。

实测：`table-grid-paint-htb-ltr` 17900 → **0**；全量屏幕 **2066 → 2065（新增 0）**；单测 2180；打印 189/198。竖排变体（vlr/vrl）仍红在轴映射（行沿 x 堆、列沿 y 排、vertical-rl 自右向左），是下一片。

### 9.46 `<col>` 背景按“行带 × 列”画；RTL 表格的列序自右向左

`table-col-paint-htb-ltr` 的 ref 是两条绝对定位色带（`inset-block-start: 60px; inline-size: calc(50% - 15px)`，高 255），对应“第一列单元格区从顶行到末行”的带宽——我们此前根本不画 `<col>` 背景。Chrome 实测（`border-spacing: 10px` 的两行两列、单元格透明）：列背景**只在单元格带内**、行间距留白且露出表格背景，且**每一行带渐变重新起相**（y=10 与 y=50 各从 0 开始），列间 spacing 也不画。

实现：`TableFormattingContext.ArrangeColumnBands` 在行盒之前插入 `(列 × 行)` 的 LayoutBox（`Kind=Block`、`SuppressBorder`、仅背景），列盒取自 `col`/`colgroup`；分片时它们作为表格子盒处在表格的 `FragmentClip` 之内，随片裁切。顺带两处缺口：

- **RTL**：`PlaceColumns` 增加 `reversed`，`direction: rtl` 的表格首列在最右（`X = total - cursor - width`）；此前列序恒为 LTR，`col-paint-htb-rtl` 只是恰好两页都不画列背景才空过。
- **`col span`**：`CollectColumnHints` 按 `span` 重复登记同一个 `col`/`colgroup`（HTML §4.9.11），`table-col-span-1` 的 `col span=2` 此前只覆盖一列。

实测：`table-col-paint-htb-ltr` 17150 → **0**，`col-paint-htb-rtl` 与 `table-col-span-1` 恢复；全量屏幕 **2065 → 2064（新增 0）**；单测 2180；打印 189/198。

### 9.47 剩余 wpt/css-break 失败的三块缺口（2026-11-01 实测记录）

当前 css-break 仍红 67 对（table 19、flexbox 16、grid 2、顶层 30）。逐页 dump 后的结论：

1. **竖排表格 = 两个缺口叠加**（row/section/col/grid/collapsed 的 vlr/vrl 共 10 对）。`table-row-paint-vlr-rtl` 的 test 页里表格量成 `34x100`（把行内轴当成了宽），而 ref 页的 400×100 相对块**整个没有被切开**——竖排多栏目前不做“沿块轴（x）切一个宽于栏的子盒”。两者都要做：表格网格按书写模式转置（列的内联尺寸→物理高、行的块尺寸→物理宽、vertical-rl 自右向左），以及竖排多栏的子盒块轴切片。Chrome 对这两页逐像素一致（已验），所以 ref 是可用的基准。`break-inside-avoid-min-block-size-2` 同属竖排多栏（其子盒被塞进不同“列”而不是沿 x 堆叠），一并卡在这里。
2. **flex 容器的跨栏重排**（059–061、065–071、093–096 等 ~12 对）。`multi-line-column-flex-fragmentation-059` 里 110 高的 column flex 在 60 高的栏里**没有被切开**（`IsBareWrapper` 只放行无边框/背景的裸 flex），整盒溢出；Chrome 把它切两栏，且**每片单独做 flex 排布**（space-between 只在片内分布、断点处的 gap 截断、边框只在外缘），所以“整盒排一次再裁”不够，要让 `FlexFormattingContext` 接受“项范围 + 片高”重算计划。
3. **“理想 ref”用例不能拿 Chrome 当真值**（out-of-flow-in-multicolumn-045/073/090 等）。045 的 Chrome 实测 `getBoundingClientRect` 显示 abs 的碎片并集为 `(8,50) 100x100`，与 ref 的普通绿方块**恰好一致**，但 Chrome 的 test 页截图与 ref 仍有差异（这些是 Chrome 已知 bug 的测试）；我们若要过，只能实现 ref 描述的理想语义（OOF 随包含块逐栏分片、每片在所在栏内定位），而不是照抄 Chrome 的怪癖。

下一步建议顺序：② flex 容器重排（机制清晰、ref 无怪癖、可先只做 column flex 拿 059–061）；① 竖排转置（10 对，但牵动表与多栏两个核心）；③ 逐对实现理想语义（3 对，需先定“每栏内定位”的规范解释）。

### 9.48 带装饰的 column flex 跨栏：每片各自是一个 flex 容器

`multi-line-column-flex-fragmentation-059/060/061` 的 flex 有边框和背景，`IsBareWrapper` 不收，于是整盒 114 高溢在一个 60 高的栏里。Chrome 的做法（JS 探针实测）：flex 的**内容**高 110 参与平衡（每栏 55），每片是 `55 内容 + 自己的边` 的盒子（57），且每片的项**沿用整盒排布的间距**、只把**首项重锚到片首**：059（space-between）两片的项都在 0 与 28.33（gap 3.33 = 整盒 10/3）；060（space-around）首片保留 1.25 的前导空间、次片为 0、项距 2.5；061（space-evenly）首片 2、次片 0、项距 2。

实现：
1. `MulticolChildren` 对“非行向、有内容、无 OOF/浮动/强制断点”的 flex（高出栏者）置 `Relays`（并入 `Divides` 走“就地起切 + 续排”），`FragmentChild` 用**内容深度**并带 `Splits`；
2. `Balance` 对 `Splits` 子盒的下限改为 `height / 栏数`（否则 114 高的子盒永远平衡不出 55）；
3. `ArrangeMulticolRun` 的预切片分支改调 `LayoutEngine.ArrangeFlexFragment`；
4. `FlexFormattingContext.ArrangeFragment` 用**整盒计划**一次算出所有项，按块轴窗口选片内项，`shift = 首项块向偏移`（首片为 0，保留整盒前导空间），项尺寸沿用整盒；片的边框盒高 = 片内容 + 该片自己的块首/块尾边。

排除项（实测）：行向 flex（`065` 类，窗口应按行读，尚未写）、带 `break-before/after` 强制的 flex（`044`）、竖排容器内。`Relays` 不改变栏数上限：仍受 `FitsTheColumns` 约束。

实测：059 6000、060/061 各 6000 → **0**；全量屏幕 **2064 → 2061（新增 0）**；单测 2180；打印 189/198。行向 065–071 仍红（065 42600 → 28200，未收敛，已回退行向部分）。

### 9.49 行向 flex 跨栏：按行窗口切，片高为整栏、首窗保留前导空间

`multi-line-row-flex-fragmentation-065/067/070/071/094/095` 是带 `row-gap`/`align-content` 的换行行向 flex。Chrome 实测（JS 探针 + 截图）：

1. **窗口按行切**：片从“上一片装不下的第一条行”开始，`gap` 在断点处截断——下一行的行首就落在新片顶部（065 的 100 高栏里每片只放一行，4 行 = 4 片，且**溢出到声明的 2 栏之外**仍照画）；
2. **片高 = 整栏**（065 每片 100 高，灰色背景一直铺到栏底，即使内容只有 50）；
3. **首窗从容器起点开始**（保留 `align-content` 的前导空间，095/096），断点后的窗口从首行开始（前导空间被截断）；
4. 行向 flex 里 `break-before: always` 是**容器内的换行**而非分片断点（067），不能据此拒切；
5. 只有**声明了块向尺寸**的 flex 才走窗口：自动高的 flex 与同尺寸的普通块一样整体摆放（`gecko/flexbox/pagination/flexbox-multi-row-1a` 的 ref 就是这么画的）。

实现：`FlexFormattingContext.Windows` 用整盒计划取行偏移并按栏高分窗；`MulticolChildren` 为每个窗口造一个 `Offset≥0` 的预切片（片高 = 窗口内容，排布时再加片自己的边）；`ArrangeMulticolRun` 里预切片 relay 用“整盒计划 + 首行重锚”摆放；`extents` 让每个预切片各占一栏（平衡容器可越过声明栏数）；`MulticolExtent` 的平衡结果按容器声明高**封顶**（顺带转绿 `gecko/columns/columnfill-overflow` 与 `multicol-span-all-children-height-003`）。

实测（两步）：先只放“声明高”的容器，**094、095 转绿**（另有两条非 flex 对随之转绿），全量屏幕 2061 → 2057；随后把自动高容器也放进来、但**只在其每行恰有一项时**走窗口模型（行内多项的行跨界尚未实现，`gecko/flexbox/pagination/flexbox-multi-row-1a` 的 ref 就是“每行两项且整块摆放”），**065/067/070/071 一并转绿**，全量屏幕 **2057 → 2052（新增 0）**；单测 2180；打印 189/198。096（行跨界被切开）、068/069/093 仍红，是下一轮。

### 9.50 平衡器的最小片高：行数不够孤儿+寡行的段不可切

`widows-orphans-002`（绝对定位的收缩宽多栏，两个 `X<br>X` 段落、`widows:2`）应平衡成 2×2；我们在 4 栏各放一行。原因是平衡器的下限只取“最高子盒”（一行 17.72），于是 1 行高的候选片被接受；而两行一段、orphans 1 + widows 2 超过段长，**任何切法都不合法**（`KeepsRun` 里孤儿下限会压过寡行，切出 1+1）。

修法：`Fragmentainer.Indivisible` 找出“行数 < orphans + widows”的段，把它的**整段高度**作为平衡下限；这类段在平衡搜索里视为不可切（仍可在最后一片溢出）。限定在平衡路径（`Balance`），`column-fill: auto` 的填充仍按 `KeepsRun` 的既有优先序。

实测：`widows-orphans-002` 248 → **0**；全量屏幕 **2057 → 2056（新增 0）**；单测 2180；打印 189/198。

### 9.51 多栏里的 grid 也按“整盒排布 + 裁剪”跨栏切

`grid-container-fragmentation-009`：100 高的两栏里放一个 200 高的 grid（两行 50/150），Chrome 把它切成两片、轨道位置不变。grid 的轨道是**绝对位置**，所以不需要像 flex 那样逐片重排：沿用 `Divides` 的“整盒排布一次 + `FragmentClip`”即可。改动只是让 `DividesAcross` 也收 `BoxKind.Grid` 的、有子盒、无 margin 的容器。副产物：`wpt/css-gaps/grid/fragmentation` 的 8 对与 `subgrid/...-008` 一并转绿（它们的 ref 正是“分片后的 grid 间隙装饰”）。

实测：全量屏幕 **2052 → 2042（新增 0，10 对转绿）**；单测 2180；打印 189/198。竖排 grid、`grid-item-oof-013`（分片 grid 内的绝对定位项）仍红。

### 9.52 当前基线与剩余缺口（2026-11-01 收口记录）

屏幕全量失败 **2033**（本会话起点 2066，累计 33 对转绿）；`wpt/css-break` **50**（table 17、flexbox 5、grid 0、顶层 28）；单测 2180；打印 189/198。已推送的切片：`4570b6a4`（表格盒/caption/就地起切）、`d89d381b`（列背景 + RTL 列序 + col span）、`3c21d520`（column flex 逐片重排）、`85e9e4ee`（行向 flex 窗口）、`55e1992c`（平衡器不可切段下限）、`230bcf7b`（自动高行向 flex 单行一项）、`c1b4726f`（多栏 grid 跨栏切分，10 对）。

实测并回退（勿重复）：多栏自身文本行的 fill 分栏（`FragmentSlicer.Fitting` 的 orphans 让位）——修好 `text-in-nested-multicol` 的文本分栏却零对翻转；表格分区全量排序（所有 thead→tbody→tfoot、按计算 display 判角色）——目标页 851→441 但弄红 `css-tables/row-group-order`；行向 flex 行跨界切开——096 7000→560 但 065/067 的 ref 页同被重分片而巨差。

剩余三块（都需要新机制，按收益/难度）：
1. **多栏内 float 带**（约 10 对：float-009/017、line-after-unbreakable-float、widows-orphans-020..023、tall-float-pushed、overflowing-block-003、fragmented-autowidth）。`ColumnsOf` 现以 `floats>0 && flowing>0` 拒绝分栏；需要“栏内 float 占道、后随行盒绕排”，并与 `FloatTracks` 的按栏记账接通。
2. **竖排表格轴映射 + 竖排多栏块轴切片**（约 13 对：table 的 vlr/vrl paint 10、collapsed vlr/vrl 2、`break-inside-avoid-min-block-size-2`）。表格网格需把“列内联尺寸→物理高、行块尺寸→物理宽、vertical-rl 自右向左”；竖排多栏还需沿块轴（x）切宽于栏的子盒（当前把子盒塞进不同栏而不是沿 x 堆叠）。
3. **repeated-section 的表头/表脚重复**（4 对）：需给 `FragmentSlice` 加片段偏移/高度，让表格在每个片段顶部重画表头并**从该片可用高度中扣除**，即“嵌套 fragmentainer + 重复区段”。

小项：`grid-item-oof-013`（分片 grid 内**孙级** abspos 按 `grid-row-start` 取区域）；box-shadow-002/004/005 是分数坐标的抗锯齿差（按规则跳过）。

**声明高的平衡容器“也可切”试过并回退**：`flex-container-fragmentation-006`（空 flex、`height:400`、`margin-top:200`、4 栏 100 高、默认 balance）里 `AddBoxCut` 因 `fills=false` 拒切；把“有声明高的平衡”视作 fill 后，该对 12800→5800（仍红），却弄红 `css-overflow/overflow-clip-margin-mul-column-*` 两对，按门禁回退。另外量到 Chrome 对“上边距超过栏高”的块会把该边距**截断为 0**（flex 从首栏顶开始），这一步单独做亦未翻绿。

**`grid-item-oof-013` 试过整条链路并回退（未提交）**：①融合样式对 OOF 盒也读 `GridItem`（原来只在 `parent.InGrid` 时读）；②`GridFormattingContext` 记录“孙级 abspos 的网格区域”（走 `OutFlowArea`，停在就近的 frame）；③`PositionedLayout.Arrange` 命中该区域时替换包含块；④`HasOutOfFlowChild`/行线映射认这些后代（否则 `plan.RowLines` 为空、区域退化为整盒）；⑤只给起始行时按规范跨一条轨道（仅对这条后代路径生效）。结果：目标对与 4 个 `css-grid/abspos` 转绿，但 `descendant-static-position-003`（孙级静态位置）与 `subgrid/abs-pos-001` 变红，净 +2 但门禁要求 0 新增，整片回退。下一轮需先弄清 Chrome 对“孙级 abspos 静态位置 + 网格区域”的优先序（003 的 abs 盒没有 inset，静态位置与区域的夹取顺序不同）。

### 9.53 片段里的重复区段 + fill 多栏自身行的 orphans 让位（两者缺一不可）

`repeated-section/text-in-nested-multicol` 的 ref 是一个 `columns:6; height:20; width:fit-content; column-fill:auto` 的六栏 div（"h a h a h a"），test 是表格（表头组里套 2 栏小多栏）。两页各自缺一条：

1. **ref**：`column-fill: auto` 的**自身文本行**在“一片只装得下一行、默认 `orphans:2`”时，`FragmentSlicer.Fitting` 返回 0，`FillLines` 的 `spills` 分支把整段行塞进第一栏。改为让 orphans 让位于栏高（`HeightFit`：只按高度取行、至少一行）。
2. **test**：表格的重复表头仍画在**整盒**顶上（片段 2/3 落在 y=-50/-150，看不见）。给 `FragmentSlice` 加 `Offset`/`Height`（`ArrangeMulticolRun` 的预切分支传入 `child.Offset`/`piece height`），`TableFormattingContext` 在非首片顶部重画表头组、在“表格未结束”的片段底部重画表脚组：副本是整张表的又一次排布，按区段带（`gridY + row.Y` 起、行高之和）`FragmentClip` 裁剪。

关键：两条单独落地时都**零对翻转**（ref 或 test 各自仍错），合在一起该对才转绿。

实测：全量屏幕 **2042 → 2041（新增 0）**；单测 2180；打印 189/198。`variable-fragmentainer-size-001/002` 与 `balanced-inner-multicol` 仍红——它们还需要**嵌套 fragmentainer**（`columns:1; height:250` 的包装自己就是分片容器，表头要在“包装的每一片”里重复），下一步。

### 9.54 换行 flex 即便只有一行，行高也是最高项（不是容器高）

`single-line-row-flex-fragmentation-050`：`flex-wrap: wrap`、高 50 的 flex 里放一个自动高项（100 宽）和一个 100 高的项；ref 是 100x100 绿方块。我们让自动高项拉伸到容器内容高 50（`ResolveCrossSizes` 在“单行 + 确定高”时把行高直接设为容器内高），而按 CSS Flexbox 1 §9.4 第 8 步，“单行容器之行高 = 容器内高”只适用于**不换行**的容器；换行容器（哪怕只产出一行）的行高取该行各项外高最大值（100），自动高项随之拉伸到 100、溢出盒子 ✓ Chrome 亦如此。

实测：`single-line-row-flex-fragmentation-050` 5000 → **0**；全量屏幕 **2041 → 2040（新增 0）**；单测 2180；打印 189/198。

### 9.55 裸包装“内容比一栏还高”时也要展开，让栏切其内容

`widows-block-in-inline-001`：`<span><div>p2（7 行）</div></span>` 里的块被 hoist 成 span 下的块，span 是一个**裸包装**（无边框/背景/尺寸）。`SpliceOverset` 原本只在“裸包装声明的块高小于其内容”时把它展开；这里高是 auto，包装与其内容等高，于是整块被放进一栏、7 行全部溢出，而不是按行分到 2、3 栏。

修法：`SpliceOverset` 再收 `fragmentHeight`——裸包装（`IsBareWrapper`）的**内容高超过一栏**时同样展开，其子块随行切片分栏（包装本身不画任何东西，也不构成断点，CSS Break 3 §4.1）。

实测：`widows-block-in-inline-001` 889 → **0**；全量屏幕 **2040 → 2039（新增 0）**；单测 2180；打印 189/198。

### 9.56 孙级 abspos 的网格区域：三条判别条件让它与静态位置/子网格共存

`grid-item-oof-013`（分片 grid 里，**孙级**绝对定位项按 `grid-row-start` 落在对应行）需要整条链路：融合样式对 OOF 盒也读 `GridItem`（原来只在 `parent.InGrid` 时读）；`GridFormattingContext` 在排布时记录“后代 abspos 的区域”（走 `OutFlowArea`，`spanOne: true`——只给起始行时按规范跨一条轨道）；`PositionedLayout.Arrange` 命中该区域时替换包含块；行线映射对所有**排布 pass** 都补齐（`placed = forOutOfFlow`，只省成本、不改语义）。

前两次尝试分别弄红 `positioned-grid-items-022`（直接子盒的 auto 末端）与 `descendant-static-position-003`（孙级**静态位置**）、`subgrid/abs-pos-001`（子网格借线）。最终加的判别条件：
1. `spanOne` 只对**后代路径**生效（直接子盒仍由 `ArrangeOutOfFlow` 走原逻辑）；
2. 记录时**不进入嵌套 grid**（子网格自己会记自己的后代）；
3. 只记录“**两轴都有命名**（`NamesBoth`）**或**盒自身带 inset（`HasAnInset`）”的后代——只命名一轴且无 offset 的盒由静态位置摆放（003）。

实测：`grid-item-oof-013` 与 `css-grid/abspos/positioned-grid-descendant-containing-block-009/010/013`、`positioned-grid-descendants-017` 共 **5 对转绿**；全量屏幕 **2039 → 2034（新增 0）**；单测 2180；打印 189/198。

### 9.57 行向 flex 的窗口触发看“窗口数”，不看盒高

`multi-line-row-flex-fragmentation-068`：flex 自身声明高 25（比一栏 100 还矮），但行高 150 + row-gap 100 的内容溢出到 300。原触发条件 `extent > columnHeight` 因此不成立，整盒放进一栏、内容全溢出。改为让 `PlanMulticolRun` 之外的那一层**只问窗口个数**：`windows.Count > 1` 才跨栏（窗口由整盒计划的行偏移与栏高算出，与盒自身的声明高无关）。行内多项目的自动高容器仍只在其“每行恰一项”时才走窗口。

实测：068 14200 → **0**；全量屏幕 **2034 → 2033（新增 0）**；单测 2180；打印 189/198。

### 9.58 尖角扩散阴影：方角保持方角，且与边框一样按整像素落墨

`box-shadow-002/004/005` 三对（“Box shadows should not be painted at column breaks”，参考页用 10px 边框画同一形状）有两处差异：

1. `CornerRadii.Spread` 无条件把 spread 加进每个角的半径，0 半径的方角因此被画成圆角；Chrome 实测（50×50 方盒 + `0 0 0 10px`，四角 (12,12)/(15,15)/(19,19) 全黑）与规范一致——方角随边移动仍为方角，与 `Grow` 的既有规则相同，改为同样判 `IsZero`。
2. 阴影与边框从同一条分数边起画时落墨不同：Chrome 实测两者都按设备像素取整（73.72 → 74，63.72 → 64，133.72 → 134），而 Hutu 只给背景/边框对齐（`Fill` 与 `DrawBorderSides` 的 `SnapToDevice`），阴影仍带抗锯齿，于是切口行/角上留 1px 缝。`DrawBoxShadow` 在**零模糊且零圆角**时对 `Area`、`Shape` 与 `Band` 一并 `SnapToDevice`；模糊或圆角阴影保持软边原样。

实测：css-break `box-shadow-002` 320 → 0、`004` 1040 → 0、`005` 720 → 0；另带绿 `css-backgrounds/box-shadow-039/040/041/042/-outset-without-border-radius-001/-overlapping-001/002/003` 与 `gecko/box-shadow/boxshadow-spread` 共 9 对。全量屏幕 **2033 → 2021（新增 0，修复 12）**；单测 2180；打印 189/198。`box-shadow-003`（vertical-rl）仍差 10860：`BandOf` 把块轴起点当作低坐标，而 vertical-rl 的块起点在高坐标一侧；同时该对 ref 的竖排 abspos 静态位置本身偏 +100，两者要一起量。

### 9.59 空白子节点不是内容：clone 盒照样能被栏切开

`box-decoration-break-clone-009`：200 高、`box-decoration-break:clone`、带 10px spread 阴影和 5px outline 的盒写成了两行（`<div …>\n  </div>`），标签之间留下一个纯空白文本节点。`AddBoxCut` 的门槛用 `child.Children.Count > 0` 判断“盒里有东西、先破内部”，纯空白节点让它以为有内容，于是整盒留在一栏、越过栏高溢出；同一盒写成一行（无空白）时则正常被切成两片。Chrome 上两者一致：空白不构成行盒，盒仍按声明高被切。

修法分两步：
1. 新增 `HoldsContent`：只有“能造出行盒或盒子的子节点”才算内容，纯空白文本不算；门槛改为“有内容”而不是“有子节点”。
2. 放宽先只给 **clone** 盒：`box-decoration-break:clone` 的每一片本身就是一个完整的盒（背景/边框/阴影/outline 都属于该片，CSS Break 3 §4.7），切它不涉及把一条边在几片之间分配，因此裁切路径里 `cutClones` 时按“片自己的矩形”排布、`whole` 取该片、不加 `FragmentClip`；`slice` 的盒仍整体留下——实测把 slice 盒也切开时 `gecko/bugs/379349-2a`（底边框落到下一栏）、`CSS2/floats-clear/floats-clear-multicol-001` 两对变红，切片语义下边的分配要另做一片。

实测：`box-decoration-break-clone-009` 8826 → **0**；全量屏幕 **2021 → 2020（新增 0）**；单测 2180；打印 189/198。`box-decoration-break-clone-split-inline`（行内盒被块级子盒打断）仍差 35314，与 `block-in-inline-015` 同属“行内盒拆成多段”的记号问题。

### 9.61 float 跟在该行内容之后：`LineWidths` 接线 + 四条限定

`float-009`（`A<div float>X</div>` 要排成 `AX`）：float 原先落在行的下方 `(8,58)`。接通早已落地的 `InlineLayout.LineWidths`（此前无调用方），在 measure 与 arrange 两个 pass 的 float 分支里取“前一个匿名行块的最后一行”：行的顶（`bottom - 末行高`）与内容到达处（`Line.Used`）。`FloatTracks.Place` 的 `inlineOffset` 只作用于**第一次尝试**（行所在行）；float 放不下时落到下一行从边开始，而不是带着偏移一路下移。

四条限定（都在实测中各自救回一对）：
1. `LineWidths` 原来返回 `Line.Extent`（换行用的整行宽），改为 `Line.Used`（内容实际到达处）——否则 `offset=100`，float 永远放不下；
2. `Before` **不跳过 float**：紧邻的前一个若是 float，不贴行放置（`gecko/bugs/50630-3`：“float 不得高于源码更早的 float”）；
3. 前一个匿名块若以 `BoxKind.LineBreak`（`<br>`）结尾，则**没有可跟随的行**，float 落在断行之后那一行（`flexbox-baseline-multi-line-vert-001` 的 float 版 ref）；
4. 贴合放置前要问 `FloatTracks.FreeSpanAt(行顶)`：该行被既存 float 挤窄时，float 放不下就保持原落点（`gecko/bugs/50630-4`：蓝 float 应在被绿 float 挤窄后的下一行行首 `x=108`，而不是抬到上一行）。

实测：`float-009` 12500 → **0**；带绿 `background-clip/clip-text-stacking-context-child-002`（其 ref 用 float 模拟行内盒）；全量屏幕 **2020 → 2018（新增 0，修复 2）**；单测 2180；打印 189/198。

### 9.62 栏里可以有 float：`ColumnsOf` 不再拒绝“float + 流动内容”

`widows-orphans-020`（一栏 100 高：20px 块 → 100% 宽 80 高 float → `&nbsp;<br>` 行）原先整条多栏被 `ColumnsOf` 的“float 与其他流动盒并存”拒绝，退化成普通块：float 与行按 190 宽排、绿色盖到第二栏外。按测量放开该拒绝：float 与普通块一样作为 `MulticolChild` 进入某一栏（占掉该栏余下高度），其后的行因放不下进入下一栏；全宽 float 不需要行内 band，故本步不需要逐栏 float band。

实测：`widows-orphans-020` 9000 → **0**，连带 `gecko/bugs/563584-9d` 转绿；全量屏幕 **2018 → 2016（新增 0，修复 2）**；单测 2180；打印 189/198。`widows-orphans-021/022`（float 在**被行切开的子盒内部**，需要逐栏 float band）仍差 6250 / 8750；`overflowing-block-003`（float 在栏内与文字互动）同属后续片。

### 9.63 剩余簇的现场测量（021/022、093、table-border-008、variable-fragmentainer）

- `widows-orphans-021/022`：Chrome 实测（021 的 fragment rects）`div` 有两个片段 `(8,50,50x100)`、`(58,50,50x100)`——**每一栏的片段都是满栏高**，内容按内部块/行边界分配到两栏（25px 块在第 1 栏；`<br>` 行、float、`&nbsp;` 行、`<br>` 行在第 2 栏的不同 y 上）。Hutu 该子盒根本未被切开：它有**块子盒**，`FragmentsInColumns` 为假、`DividesAcross` 为假，整盒 50x125 溢出。要点是“子盒内部递归分片”（与 `overflowed-block-with-no-room-after-000/001` 同源），不是 float band。
- `multi-line-row-flex-fragmentation-093`：100 宽换行 row flex 里第 2 项自带 210 高溢出子盒 ⇒ 单行高 210，需按**项窗口**（0..100/100..200/200..210）逐窗重排；当前 `Windows` 只按行切，整行进一个窗口。
- `table-border-008`：表 50 宽 + 10 边框、上下各 150 caption、总高 480，在 6 栏（每栏 100）里“切成三栏”。ref 在**第 3、4 栏顶**都要黑边框：折叠/切片的表在其 caption 分片处也要画出表盒边框，Hutu 只在首栏画。与折叠表“外侧半线”几何（§9.60）同源。
- `table/repeated-section/variable-fragmentainer-size-001`：外层 3 栏切内层 `columns:1;height:250px` 容器，表（thead 20 / tfoot 20 / 内容 130）在**每个内层片段**都要重画页眉/页脚；Hutu 的内层容器未被外层切出多片，故只画一次。属“嵌套片段的重复区段”。

全量门禁复核（HEAD `94a7d647`）：屏幕失败 **2016**（gecko 603 + wpt 1413；`wpt/css-break` **44**）、单测 2180、打印 189/198。

### 9.64 余下 44 对的分档（本轮逐项测量后）

- **字形 AA（1–2px，按规则跳过）**：`out-of-flow-in-multicolumn-093`（28px，`::before` 项目符号比 ref 高 1–2px）、`sections-and-captions-mixed-order`（899px，绿底文字的抗锯齿相位）。
- **嵌套片段（子盒内部的递归分片）**：`widows-orphans-021/022`、`overflowed-block-with-no-room-after-000/001`（外层 80 高盒在 70 高栏内切开、内层 140 再切）、`table/repeated-section/variable-fragmentainer-size-001/002`（内层 `columns:1` 被外层切片、每片重画表头/页脚）、`balanced-inner-multicol`（700px：反复的页脚被画到了首片顶部，应在各内层片段的**底**）、`fieldset-001`。
- **表格片段几何**：`table-border-008`（Chrome：表盒 5 个 70×100 满栏片段；caption 片段 `(8,50,70×100)+(108,50,70×50)` 与 `(308,100,70×50)+(408,50,70×100)`；表盒边框/背景按片段铺，Hutu 的 `TableBox` 排除 caption 故环只框 20 高网格）、`table-collapsed-borders-paint-htb-ltr/-at-boundary`（Chrome 实测：`tr height:50` 的行盒 50 = 5(内半) + 40 + 5；表盒首行上方另有 5px 外侧半线、caption 在表盒之外，故 ref 的假列 div 比行盒高 10）、及其竖排两对。
- **竖排表格轴映射**：`table-{col,row,section,grid}-paint-vlr-rtl/vrl-rtl` 8 对（Hutu 里表 34×100、单元格 2×35、caption 高 0）。
- **flex 分页**：`093`（Chrome 实测：容器 3 个 50 宽片段 `(8,8,50×100)+(58,8,50×50)+(108,8,50×0)`；item 按块向逐窗分片——橙项内 210 高子盒分 `(33,8,25×100)+(83,8,25×100)+(133,8,25×10)`，蓝项分 `(8,58,25×50)+(58,8,25×50)`）、`069/096`、`044`（零高栏推首项到下一栏并按块首截断 margin）、`flex-container-fragmentation-006`。
- **OOF 流坐标分片**：`abspos-in-opacity-003`、`out-of-flow-in-multicolumn-045/073/090`、`box-shadow-003`（竖排带 + ref 竖排 abspos 静态位置）、`block-end-aligned-abspos-with-overflow`。
- **float 与多栏**：`float-017`（663px，嵌套 flow-root 里的 float 带）、`fragmented-autowidth-fc-root-beside-floats`、`tall-float-pushed-to-next-fragmentainer-002`、`overflowing-block-003`、`line-after-unbreakable-float-after-padding`（312px，同上字相位）。
- **transform**：`transform-006/008`（分片各自计算 transform-origin）。
- **行内拆段**：`box-decoration-break-clone-split-inline`（35314，与 `block-in-inline-015-print` 同源）。
- **竖排其它**：`break-inside-avoid-min-block-size-2`（123400，竖排多栏里 break-inside:avoid + min-block-size）。

### 9.65 行向 flex：栏可以在行中间切开，下一栏从切点续画

`multi-line-row-flex-fragmentation-096`（`align-content:space-evenly`、180 高、3 行×50、栏高 60）的 Chrome 真值：行 1 在首片（保留首端分布间隙 7.5），行 2 从次片顶部（间隙被断点吞掉），**行 3 被栏切开**——次片底部显示 2.5px、第三片顶部续 47.5px；三个容器片段宽 144、偏移 0/60/120。三处配合才闭合：

1. `Windows`：当窗口已有一行、而剩余空间**停在下一行内部**（`lines[index].Start < start + room`）时，窗口在 `start + room` 处切，下一窗口起点带上 `carry = cut - 下一行起点`，该行因此从窗口顶部上方 `carry` 处开始，只露尾巴；若余量停在**行间隙**里（`065/067` 的 `row-gap:100px`），不切，保持旧行为——这条判别器正是历史上 `cursor = end` 方案弄红 065/067 的原因。
2. `ArrangeFragment`：成员归属改为**相交**（`at < end && at + size > start`），并让 `shift` 取 `max(首成员, start)`，被切的行才不会被当成上一片的成员而丢掉。
3. `ArrangeMulticolRun` 的 relay 分支：给窗口片段设 `FragmentClip = 窗口矩形`（此前 relay 片段从不裁剪，交叠行在 60 高的栏里画满 50px 并溢出）。

实测：`096` 7000 → **0**；`065/067` 保持绿；`069` 21300 → 17750（行数不同，尚需项窗口）；`093` 8000 不变（需按项位置窗口，见 §9.64）。全量屏幕 **2016 → 2015（新增 0，修复 1）**；单测 2180；打印 189/198。

### 9.66 再量三簇：069 的“同栏多片段”、`clone-split-inline` 的块内行内装饰、transform-008

- `multi-line-row-flex-fragmentation-069`：ref 页把同一 flex 的**多个片段放进同一栏**（第二栏从顶到底全是青色，行距 100 在断点处被截断为 0），而 test（`row-gap:100px`）的 flex 整体因“剩余 75 放不下 100”先移到第二栏再按无间隙堆叠。现有 relay 是“一窗一栏”，要闭合需允许“同栏内多个窗口续排 + 断点截断行距”。
- `box-decoration-break-clone-split-inline`（35314）：Hutu 把含块级子的 `span` 提升为**块盒**（`block span` 764×481，内含两个匿名行块与 div），于是 span 的 10px 紫边框被画成**整个块盒的外环**（(18,10) 764×481）；正确模型是行内盒被块拆开后，边框/内边距只包住两段文字所在的行片段（clone 下每段各画完整四边），绝不能围住中间的块。要闭合需要在 block-in-inline 提升时保留行内装饰按“行片段”绘制。
- `transform-008`：ref 用三列各 100% 高的假列模拟“每个分片以自身为原点旋转 45°”；test 是 200 高旋转盒在 3 栏里分片并对 100% 高的绝对子盒定位——需“按片段各自算 transform-origin + abspos 对分片包含块解析”。
- `variable-fragmentainer-size-002`（800）：与 001 同源，内层 `columns:1;height:250` 被“首栏只剩 50、放不下 20px 页眉”切开后，页脚应出现在第三栏顶部（ref 的 (208,50) 热粉方块）；Hutu 的内层容器未被外层切片。

### 9.67 `069` 的完整规则与 `fieldset-001-grid-print` 的断点消费者

- **`multi-line-row-flex-fragmentation-069`**：Chrome 的判定用 **flex 整体高**（不是首个窗口）：`100`（ref）或 `200`（test，含 row-gap）都放不下首栏剩余的 `75`，于是整个 flex **整体下移到第二栏**；进入新栏后按“断点截断行距”重算窗口——两行各 `50`、无间隙地堆叠在同一栏（ref 第二栏从上到下全青）。实现路径：`MulticolChildren` 的 relay 组需作为**原子组**按 `whole` 判下移，且下移后按新栏高重算 `Windows`（行距在窗口断点处截断为 0），两件事都在规划器层，不能靠单片修。
- **`gecko/pagination/fieldset-001-grid-print`**：Hutu 打印树 2 页（页 1 内容 0..96、页 2 0..147；字段集边框 48 环绕 384×192）；ref 页用 `A<br class=pb>B` 的块盒，其页 1 片段边框延伸至 y≈192（填满分页内容区）。差在“`page-break-after:always` 在 grid 项上的断点被字段集消费后，片段边框应延伸到分页器底”，与 001 系列记载的“br 断点消费者”同源。

### 9.68 `093` 闭合：能切行的窗口放开自动块高 relay，再给 flex 项算 reach

- **窗口切行的标记 `Cut`**：`Windows` 现在报告哪个窗口把一行切成两段。relay 对“块高取自行的自动容器”原本只允许“一行一项”（`windows.Count == child.Children.Count`），理由正是当年窗口不会切开一行；有了 `Cut`，只要确有一个窗口止于行内部，自动块高容器就可以按窗分片（`multi-line-row-flex-fragmentation-093` 的首窗把 100 高的第二行切在 50 处）。判别器仍是“切行 vs 切间隙”：`065/067` 的余量停在 `row-gap` 里，`Cut` 为假，旧行为不变。
- **项的 reach**：`ItemReach` 取项自身块向尺寸与 `ChildrenReach` 的较大者，后者沿块轴逐项堆叠其子盒、量到最底，遇 `ClipsOverflow` 即止。两处消费它：`Windows` 在行用尽后按容器 reach 继续开窗（`0..100 / 100..200 / 200..210`），`ArrangeFragment` 的成员判定由“盒与窗口相交”改为“reach 与窗口相交”——橙项的盒只在 `0..50`，却因 210 高的子盒而属于第 2、3 窗，其子盒按 `-100 / -200` 的偏移进入栏内，再由 relay 的 `FragmentClip` 裁到栏底。Chrome 实测同形：紫子盒 `(33,8,25×100)+(83,8,25×100)+(133,8,25×10)`，容器片段 `(8,8,50×100)+(58,8,50×50)+(108,8,50×0)`（片段框高不含溢出，栏内推进按墨迹）。
- **限定**：`ChildrenReach` 只走 `BoxKind.Block` 且其子全为块级的盒——行内/文本的换行、flex 与 grid 的排布都不是纵向堆叠，按堆叠量会虚高而凭空多开一栏；竖排容器直接返回项自身尺寸（竖排的分窗尚未接 reach）。
- 实测（对 HEAD `808a66fa` 重新跑一份全量报告作基线，`build/_score-head2.md`）：`093` 8000 → **0**（两图逐像素一致）；`096/065/067/069` 与 `gecko/flexbox/pagination/*` 逐项像素差**不变**；`css-gaps/flex/fragmentation` 里 `-026` 9870 → 6910、`-035` 2460 → 2300（更近绿），`-015` 2500 → 2750（间隙装饰的色带随片段数变化，属 §「Gap decoration」未收的口）。全量屏幕新增失败名 **0**（1940 → 1939）、修复 `093`（`wpt/css-break` 43 → 42）；单测 2180；打印 189/198。









### 9.69 栏切盒要落在盒自身的内容单元之间：widows/orphans 进入多栏切片（`widows-orphans-023`）

Chrome 对 023（`columns:2; height:100; widows:3`；绿盒 150 高，内含一个 50 高空中盒、四个 `<br>` 行和一个 abspos 子盒）的实测：绿盒两个片段是 `(8,50,50x100)` 与 `(58,50,50x75)`，绝对子盒 `(58,125,50x25)`。**首片段仍铺满整栏**（100 高，背景画到栏底），但它的**内容只到 75**——断点被 `widows:3` 从“栏的尽头 100”前移到“尾部还剩三行”的那个行界；续排片段因此从盒内 75 开始（位移 -75），行尾落在 125，abspos 的静态位置随之落到 `(58,125)`，整幅 100×100 全绿。

三条规则合起来才闭合，缺一会留下 1250 或 6250 像素差：

1. **断点候选**：`PieceBreaks(box, width, ctx)` 沿块轴量出盒自身内容单元的结束位置——子盒若 `FragmentsInColumns`（无块子、高度自适应、不裁剪、非竖排）就取其**逐行行高**（`_inline.LineHeights`），否则递归其子盒；叶子盒整体算一个单元。遇到 float、行内内容、强制断点、`break-inside:avoid`、替换元素、其它格式化上下文（flex/grid/masonry/table）时返回 null（不猜）。
2. **选点**：`PlanMulticolRun` 的 `CutAt(child, room, height)` 在候选里取**不超过 room 的最大者**，并要求“头部单元数 ≥ orphans、尾部单元数 ≥ widows”；没有候选满足就退回 room（旧行为）。候选存在 `MulticolChild.Breaks` 里，只在 `divides && !relays && !vertical` 时计算。
3. **片段框与内容分离**：切片的**绘制高仍是栏给出的 room**（`take = min(room, height)`），**续排位移用内容切点**（`resumeAt = content`）。两处原先都用 room，所以把断点前移之后首片段只剩 75 高、栏底 25 露红——实测该改动单独使用时 1250 → 6250 变差，加上这一条才到 0。

实测：`023` 1250 → **0**；整个 `css-break` 族 209 对逐对复看，除 023 外**无任何像素变化**（`021/022` 仍 6250/8750——它们要的是“只有块子盒的盒**允许**被栏切开”，`DividesAcross` 目前只放行含绝对子盒或 grid 的盒，那是下一件事）。全量屏幕 2014 → 2013、新增失败名 **0**（`wpt/css-break` 42 → 41）；单测 2180；打印 189/198。

### 9.70 回退的测量：`overflowed-block-with-no-room-after-000/001` 需要“绘制范围/流前进量”分离与栏块首不裁剪

上片留下的 `PieceBreaks` 让栏可以按盒自身的内容单元切盒。把 `DividesAcross` 放开到“内容单元可枚举的普通块盒”（`build/_divides-units.patch`，47 行，实测后回退）：

- 000/001（外层 `columns:2; height:70; fill:auto`；子盒1 `height:80` 内含 140 高绿盒；子盒2 `margin-top:-40; height:30`）**8000 → 3000**，且 css-break 整族 209 对**无其它变化**——放开本身是安全的，只是还不够。
- 剩余 3000 = 第二栏 `x=58..107, y=90..149` 该绿而白。Chrome 真值（探针实测）：绿盒子盒两片段 `(8,80,100x70)+(58,80,100x70)`，`margin-top:-40` 的子盒2 落在 `(58,50,50x30)`——**在分栏框之上**（未裁）。
- 还差两条：①被切盒的**片段绘制范围按“内容 reach”**（140-70=70，故第二片的裁剪是 70 而不是盒自身的 10），而**后续兄弟的流前进量仍按盒自身的 80**（子盒2 = 80-40 → 栏内 -30）；②栏的裁剪只在块尾方向生效，块首之上溢出照画（Hutu 的 `FragmentClip` 是整窗矩形，会把 -30..0 裁掉）。
- 这两条与 `abspos-in-opacity-003`／OOF 流坐标簇同源（也是“绘制 extent ≠ 流 extent”），要一起做才可能翻绿；`widows-orphans-021/022` 还多一条“栏内有 float 时的单元排序”。

### 9.71 `overflowed-block-with-no-room-after-000/001` 闭合：片段的绘制范围按内容 reach，流的前进量按盒自身高度

§9.70 量到的两条缺口一起补上后转绿。做法：

1. **允许切**：`MulticolChildren` 里先算 `PieceBreaks`（单元终点），普通块盒只要单元可枚举（无 float、无行内、无强制断点、`break-inside` 不 avoid、块向 margin 为 0、非嵌套栏内、横排）就允许被栏切开——判据是“**栏放不下就得在单元之间落点**”，不是“盒自己比栏高”。
2. **绘制与推进分离**：`MulticolChild.Reach`（片段可画到的深度 = max(盒高, 最后一个单元终点)）与 `Fragment.Height`（流前进量 = 盒自身）分成两个数；`PlanMulticolRun` 用 `Painted()` 决定 `take`/`last`，用 `Held()` 给出 `MulticolPiece.Cursor`，`ArrangeMulticolRun` 的 `y` 按 Cursor 前进、`FragmentClip` 仍按 `take`。于是 000 的 80 高盒（内含 140）在 70 高的两栏里画成 `0..70` + `70..140`，而 `margin-top:-40` 的后继盒落在第二栏**上方** -30..0 并照常可见（整盒片段才裁剪，整块片段不裁）。
3. `FragmentsInColumns` 里“需要几栏”的估算同步用 `max(Reach, Fragment.Height)`。

实测：000/001 **8000 → 0**。18 对同时大幅靠近绿：`gecko/flexbox/pagination/flexbox-{single,multi}-column-1a..1f` 37600 → 14450、`-single-column-2` 22450 → 15150、`gecko/pagination/border-breaking-000-cols` 52500 → 7500、`-003-cols` 6450 → 2700、`css-align/blocks/align-content-block-break-overflow-010` 109932 → 64780、`css-multicol/column-rule-002` 3600 → 2400、`multicol-list-item-008` 781 → 756 等。**三对变差但仍在红**：`gecko/flexbox/pagination/flexbox-single-column-6` 1050 → 7600、`gecko/pagination/border-breaking-001-cols` 46000 → 76000、`css-anchor-position/anchor-position-multicol-nested-001` 5540 → 5700——收紧判据（只允许“内容比盒自身还长”的盒被切）能保住这两对的原值，但那样 001 的外层自动高盒就不再被切、且 `flexbox-single-column-2` 等 16 对的改进全部消失，故取“通用可切”这一条，把这三对记为后续要按单元终点细分的靶子。全量屏幕 2013 → 2011、新增失败名 **0**（`wpt/css-break` 41 → 39）；单测 2180；打印 189/198。

### 9.72 收紧一处：只有“因内容而被切”的盒才带 reach

`9f41abb6` 之后把 `MulticolChild.Reach` 的赋予限定到 `DividesUnits` 触发的盒；因 `DividesAcross`（含绝对子盒、grid）而被切的盒仍只带**单元表**（023 的 widows 落点要用它，撤掉就红回去 1250）。实测：`css-anchor-position/anchor-position-multicol-nested-001` 5700 → **5540**（回到基线值），其余数字与 `9f41abb6` 相同——修复 000/001、18 对靠近绿、新增失败名 0、`wpt/css-break` 39、单测 2180、打印 189/198。仍有两对因“允许切”本身变差但仍在红：`gecko/flexbox/pagination/flexbox-single-column-6` 1050 → 7600（`article` 是列向 flex 的项，项的断点应由容器定；但 `flexItems` 标记只在容器被展开成成员时才打上，带背景的容器不展开，所以按“是不是 flex 项”加护栏**实测无效**，7600 不变）、`gecko/pagination/border-breaking-001-cols` 46000 → 76000。这两对要在“单元终点”与“盒的边框带”之间再细分判据，留作下一片。

### 9.73 两处收口：栏数按盒自身高度算；OOF “整盒坐标”假设被否

- `8ce5e4c7`：`FragmentsInColumns` 估算“需要几栏”时改回只看 `Fragment.Height`（不看 reach）。000/001 仍绿（2 栏就够），而 `gecko/flexbox/pagination/flexbox-single-column-6` 7600 → **6300**、`flexbox-single-column-2` 15150 → **14400**，其余 18 对靠近绿与 0 新增失败名不变，`wpt/css-break` 仍 39。
- 试过后回退的假设：把分片盒的绝对子盒**移回整盒坐标**（`OversetOutOfFlow`，只移“有自身 inset 解析”的 OOF 子树，静态位置子盒不动，023 因此不受影响）。实测 `abspos-in-opacity-003` 6000、`out-of-flow-in-multicolumn-045` 10000 都**一点没动**，而 `block-end-aligned-abspos-with-overflow` 3000 → 3500 变差——说明这几对缺的不是“片段内 OOF 该用哪套坐标”，而是 OOF 子盒**自身**要被外层栏切开（它的盒要进入 `PlanMulticolRun` 的分片队列，而不是跟着包含它的块一起被裁）。回退，未提交。

### 9.74 `table-border-008` 闭合：没有行的表仍按声明高度撑起表盒

一行改在 `TableFormattingContext.Measure`：内容高度原先是 `网格高 + 上下 caption + 其它`，而**网格没有行可长**时 `CompleteGrid` 分不出声明高度，`height:180px` 就被丢成 0——于是表盒只剩 20（上下边框），环只框住一条空网格，整盒 320 高。改为 `max(声明高度, 网格高) + caption + 其它` 后：整盒 = 180 + 20 边框 + 300 caption = **500**，`TableBox`（ arranging 侧按 `borderBox.Height - caption` 算，无需改）= **200** ✓ 与 CSS 2.1 §17.5.3“声明高度是下限”以及 Chrome 的分片实况一致：caption 0..150 占满第一栏并在第二栏吃掉 50，表盒 150..350 切成 `50 + 100 + 50` 三片（边框按 slice 只在首片带上边、末片带下边），下 caption 继续到第五栏。实测 `table-border-008` 13100 → **0**。

顺带量到一条门禁事实：全量屏幕的**失败名集合**稳定，但少数文本敏感的页在两遍之间的像素数会抖 ±1…±144（`gecko/bidi/712600-1/2`、`wpt/css-lists/inline-block-list{,-marker}`、`css-text/word-break-normal-ethiopic`、`css-pseudo/first-line-line-height-002`、`gecko/font-face/local-styled-1`、`line-edge-white-space-collapse-002`）——判断“变差”要以**名字集合**为准，单看像素增减会把噪声当成回退。两遍对照本改动都是：新增失败名 **0**、修复 `table-border-008`、`wpt/css-break` 39 → **38**、屏幕失败 2011 → **2010**、单测 2180、打印 189/198。

### 9.75 回退但完整的测量：折叠边框要“表盒上下外半线 + 逐段线色 + 侧带外沿延伸”三条一起做完

补丁 `build/_collapsed-outer-half.patch`（225 行，含前两条半，实测后回退因为没翻绿）。Chrome 真值（`getClientRects` 实测 `table-collapsed-borders-paint-htb-ltr`）：表盒四片段各 `93x100` 在 x=21/124/226/329；caption `(21,21,93x100)+(124,21,93x20)`；**tbody 起点在 flow 125**（不是 120）；TR 片段 `(129,46,83x50)`、`(129,96,83x25)+(231,21,83x100)+(334,21,83x20)`、`(334,41,83x50)`、`(334,91,83x25)`。合起来 = caption 120 + 上外半线 5 + 声明行高之和 270 + 下外半线 5 = **400**。

已量对的两条：

1. **表盒带上/下外半线**：折叠时 `build.Border = Thickness(halfTop, 0, halfBottom, 0)`、`InsetsV = halfTop+halfBottom`（横向不动：声明宽度已含左右线），`gridY` 改成 **caption 先、再 Border**，`Measure` 的 `usedHeight + InsetsV` 自然把整盒抬到 400；单元格的上下各拿**半**条线（含首末行的外侧半）。
2. **纵向线逐段定色**：`outer[b]` 只累积非单元候选（行/行组/列/列组/表），`cellLeft[r][b]`/`cellRight[r][b]` 按该行覆盖登记单元两侧，单元的左右颜色/线型 = `Resolve(Resolve(outer[b], cellRight[r][b]), cellLeft[r][b])`；**线宽仍取整线**（不动列几何）。表盒外环那两条带的颜色取解析后的 `horizontal[0]/horizontal[rows]`（`build.OuterColors/OuterStyles`），不是元素自身 style。

做完这两条：`htb-ltr` 8000 → **200**、`at-boundary` 10105 → **2415**，而 css-break 表格族其余 38 对逐对复看不变、无一对变红。

第三条（决定这两对能否翻绿）：**侧边整条 10px 色带的竖向外沿要画到相邻水平线的中心**，于是各行侧带首尾相接：行1 `120..180`、行2 `180..325`、行3 `325..375`、行4 `375..400`——恰是 ref 的假 div 序列（共享线整条归**上方**行；表首行向外 5）。Hutu 现在把侧带画在行盒 `125..175` 上，蓝/黑分界比 ref 高 5px（残差 `x=329..338 test=#0000ff ref=#000000 y=41..60`）。要做的是**绘制带**而不是布局盒（否则盒内文字会位移 5px）：给折叠单元的左右边框一个 `rowTop ± half(上线)`→`rowBottom + half(下线)` 的延伸带。

### 9.76 折叠边框第三条做成了但门禁不过：完整测量与冲突点

把 §9.75 的第三条（侧带外沿）接上后 `build/_collapsed-side-band.patch`（299 行，含 §9.75 三条 + 侧带）实测：

- **翻绿 2 对**：`wpt/css-break/table/table-collapsed-borders-paint-htb-ltr`（8000 → 0）、`wpt/css-tables/collapsed-border-large-rowgroup-001`；`at-boundary` 10105 → 2540；另有 15 对大幅靠近绿：`gecko/table-bordercollapse/borderhandling-rules-border-{cols,rows}-{quirks,strict}` 216/178 → 60/90、`ttwf-reftest-borderRadius` 3121 → 372、`box-shadow-table-border-collapse-001` 8100 → 6980、`gecko/bugs/494667-2` 1616 → 816、`background-image-{row,column}-collapsed` 18583/36800 → 16507/35800、`table-cell-width-0` 882 → 576、`356774-1` 805 → 464 等。
- **变差 7 对（都仍红）**：`wpt/CSS2/tables/border-collapse-006` 17500 → 25000、`border-collapse-spanning-cells-004` 36400 → 39200、`border-collapse-empty-row` 18075 → 19650、`gecko/table-bordercollapse/double_borders` 226 → 423、`collapsed-border-large-rowgroup-002` 888 → 904、`229591-1` 1829 → 1861、`ruby-inlinize-blocks-003` 3451 → 3463。
- **门禁不过**：4 条单测红——`ACellWinsTheLineItSharesWithItsRow`、`ARowsBorderMeetsTheLinesAboveAndBelowIt` 断言**首/末行的单元吃满整条外侧线**（`Widths.Top == 2`），`AColumnGroupDrawsTheLinesAtItsEdges`、`AColumnsBorderMeetsTheLinesBesideItsCells` 断言左侧整宽；本改动把上下外侧改成“半条 + 表盒带外半”，并且 `SideBand` 把一条 `BorderCommand` 拆成上下/左右两条，`Painted()` 取到的那条左右宽为 0。

冲突点要说清：Chrome 对 `htb-ltr` 的实测是**行盒 = 声明高（50）、其上下各被半条线咬住、整条线画在行的外沿**（tbody 起点在 flow 125、侧带 120..180），而上面 4 条单测记的是“外侧整线归首/末行单元吃满”。两者只在“表自身没有边框”时才同解；`border-collapse-006`/`spanning-cells-004` 变差说明“单元吃满外侧整线”在那些形状上仍是需要的。所以下一步要么把两种读法按“外侧线是否有对手（表边框/行组边框）”分流，要么先只保留**不影响线宽**的两件（逐段定色 + 表盒外半线的颜色）把 `htb-ltr` 之外能拿的收益拿走。屏幕新增失败名在两遍对照里仍是 0（文本抖动 ±1…±144 属噪声，见 §9.74）。

### 9.77 折叠边框第二种读法也记一笔：把环画在“半线外扩带”上会与 5 条自测的内部断言相撞

`build/_collapsed-paint-band.patch`（316 行）= 上一条的三条 + 把“上下边线减半”换成**布局内缩保持原样、只把绘制的环**换成（整条线宽、盒外扩半条线的带），且仍只发一条 `BorderCommand`（带 = `LayoutBox.BorderBand` + `BandWidths`）。结果：单测从 4 条红变 **5 条红**——多出的 `AHiddenTableBorderHidesTheTablesOwnEdge` 断言 `Widths.Bottom == 1`（表自身 2px 边在 hidden 后剩**半条**），也就是说这几条自测钉的是 Hutu **内部表示**（经 `Painted()` 读到的那条命令的 `Widths`），而不是 Chrome 的可观察像素。

两条路都要动这 5 条自测的**取值方式**（它们读的是命令的 Widths，不是几何），且必须先判定：`CSS2/tables/border-collapse-006`（17500，减半方案下变 25000）到底在说“减半错”还是另有原因——那对的差异在 `at-boundary` 里也出现过。**下一步的入口**：先把 `border-collapse-006` 单独量清（它的行/列结构与 htb-ltr 的差别：是否有 colspan、是否有表自身边框），再决定“半线内缩”与“整线外扩带”哪一个才是通用读法；两版补丁都在 `build/`。

### 9.78 OOF 簇的下一个可疑点：淡显组的层界是 `20x0`

`abspos-in-opacity-003`（6000 像素差，test 在栏 1..3 画 #008000、ref 画 #1a7200）两页的绘制命令清单**形状一致**，差别只在谁铺那块 100×100 的绿地：test 的第一条就是 `fill 8,49.72 100x100 #ff008000`（不透明），ref 的同位绿地之后才被“被淡显的红切片”盖上 10% 的红（#1a7200 = 绿 89% + 红 10%）。可疑之处写在 `Painter.PaintFaded`：它用 `box.BorderBox` 作 `PushLayerCommand` 的界，而被栏切开的包装片在 003 里是 **20 宽 × 0 高**（`PushLayerCommand 68,49.72 20x0`）——零面积 save-layer 在 Skia 里可能被当成空矩形处理，组就不生效（内容与 ref 的差别正是“组没乘上去”）。已试并**量否**：把 `PaintFaded` 的 `PushLayerCommand` 界改为 `CutOf(box) ?? box.BorderBox`（片段裁剪带）后，`abspos-in-opacity-003` 仍 6000 不变、`abspos-in-opacity-000/001/002` 保持绿——零面积层不是病因。剩下的差别在“test 的第一条命令就是 100×100 不透明绿”这一事实本身：要先弄清那条绿方块在 test 里由哪个盒铺出（应为 `.abs`，但它应是 10% 淡显组里的内容、且只有 50 宽），也就是 OOF 子盒在**包含它的淡显组被切开时该由哪个片段承载**，这与 §9.72 的结论同一（OOF 子盒自身要进栏的分片队列）。

### 9.79 把折叠边框的两半拆开量：外半线+侧带**无回退**，逐段定色才是 `border-collapse-006` 的元凶

`build/_collapsed-band-noColor.patch` = side-band 版去掉“纵向线逐段定色”。实测（对 HEAD 逐项复算）：

- `table-collapsed-borders-paint-htb-ltr` 8000 → **4200**、`-at-boundary` 10105 → **6320**、`CSS2/tables/border-collapse-006` **17500 不变**（即回到基线）、竖排两对不变；**单测只红 4 条**（下面）。
- 两对的残差签名相同：`x=225/226, y=21` 处 test 画 lime、ref 画 black——即**同一条纵向线在不同行片段要换色**。这正是逐段定色能修的部分，而逐段定色又把 006 从 17500 推到 25000。
- 为什么 006 会被逐段定色伤到（本量已经定位到具体那一条线）：006 只有两行一行列，行 1 的单元要 `border-left:150 green`、行 2 的要 `border-right:100 green`；**行 2 片段的左线、行 1 片段的右线没有任何候选**。Hutu 原来的整线解析让绿色沿整条线走（006 靠这个铺满绿色才没有红）；按片段解析后那两段落到 `outer[b]`（行/列/表候选）= None → 不上色 → 露红。所以 Chrome 在这里必定是“线整条按胜者上色”，而 htb-ltr 又确实按片段换色。二者的分界只能是：**片段有候选时用片段，没候选时退回整线胜者**——这一步还没试。

4 条单测的失败值已量清，两类原因：

1. 取值器：`TableLayoutTests.Painted(view,id)` 按 `Bounds == box.BorderBox` 选命令，侧带把左右边移到另一条命令上 → `Widths.Left` 读到 **0**（`AColumnGroupDrawsTheLinesAtItsEdges` 期望 2、`AColumnsBorderMeetsTheLinesBesideItsCells` 期望 4）。
2. 表示变了：外半线改由表盒画之后，行 0 单元的 `Widths.Top` 从整条 2 变成内半 1（`ACellWinsTheLineItSharesWithItsRow`、`ARowsBorderMeetsTheLinesAboveAndBelowIt`），可观察的“该边上那条 2px 双线”总量不变。落地时这两条要改成断言“单元内半 + 表盒外半 = 整条，线型仍是胜者的 double”，而不是单元吃满。

### 9.80 折叠边框落地：外半线归表盒 + 逐段定色（无候选退回整线）+ 侧带外沿

三条一起做才闭合（§9.75–§9.79 的分量过程）：

- **外半线归表盒**（CSS 2.1 §17.6.2）：每条横向线的中点在网格线上，故外侧那半落在所有行之外。`ResolveBorders` 末尾 `build.OuterTop = horizontal[0].Width * 0.5`、`OuterBottom = horizontal[rows].Width * 0.5`，并把 `build.Border` 改成这两半（只吃块轴内缩：横向两半由边缘单元整条画出，声明宽度已含）。`TableFormattingContext.Measure` 的 `gridY` 在折叠模型下把 caption 排在表盒边框之前——caption 站在表盒外的 wrapper 里。
- **纵向线逐段定色**：新增 `outer[]`（行/行组/列/列组/表的候选，沿整条线累积）与每行的 `cellLeft[r][b]`/`cellRight[r][b]`（`rowspan` 覆盖的每一行都记），单元格的左右颜色/线型取 `Resolve(Resolve(outer[线], cellRight), cellLeft)`。**关键退回规则**：这一段没有任何候选时（`Width <= 0.001`）退回整条线的胜者——`border-collapse-006` 只有两行一列，行 2 的左线、行 1 的右线无候选，逐段独走会让绿线断掉、露出底色（17500 → 25000），退回整线后回到 17500 基线。
- **侧带**：单元格的左右边沿那条线 reach 到它与上下横向线相交处（`LayoutBox.SideBand`，`rect.Y + (行 0 ? -top : top)`、高度加上下半），绘制时拆成两条命令：上下环（`Thickness(top, 0, bottom, 0)`）+ 侧带（`Thickness(0, right, 0, left)`）。

实测（逐对复算）：`wpt/css-break/table/table-collapsed-borders-paint-htb-ltr` **8000 → 0**（绿）、`wpt/css-tables/collapsed-border-large-rowgroup-001` **绿**、`-at-boundary.tentative` **10105 → 2540**、`wpt/CSS2/tables/border-collapse-006` **25000 → 17500**（回到基线，仍红）、竖排 `table-collapsed-borders-paint-vlr-rtl` **19380**、`-vrl-ltr` **20350** 不变（竖排表仍缺轴支持，见 §9.40）。

4 条自测按可观察量重写（不是放宽）：新增 `TableLayoutTests.PaintedSides(view,id)`——按 `Bounds.X`/`Width` 等于单元盒、且 `Widths.Left + Widths.Right > 0` 选侧带命令（`AColumnsBorderMeetsTheLinesBesideItsCells`、`AColumnGroupDrawsTheLinesAtItsEdges` 用它读左右）；上下两半改成断言“单元内半 + 表盒外半 = 整条”（2 与 3），并加一条“表盒那半仍是胜者的 `double`”（`ACellWinsTheLineItSharesWithItsRow`）。

全量门禁：单测 **2180** 绿；打印 **189/198**（九个失败名一字不差）；屏幕全量失败名集 **2010 → 2008**，`--- regressed ---` 为空，`--- fixed ---` 正是 `wpt/css-break/table/table-collapsed-borders-paint-htb-ltr` 与 `wpt/css-tables/collapsed-border-large-rowgroup-001`，`wpt/css-break` **38 → 37**。基线由 `_diff_full.sh` 现跑现取（回退切片跑 HEAD、再复原跑切片），报告存 `build/_score-head-cl.md` 与 `build/_score-cl2.md`。

### 9.81 竖排表的轴（下一片的地面真值，Chrome 已量）

竖排表那 8 对（`table-grid/row/col/section-paint-vlr-rtl|vrl-rtl`、`table-collapsed-borders-paint-vlr-rtl|vrl-ltr`）的根因是 `TableFormattingContext` 完全不看 `Flow.IsVertical`：行永远沿 y 堆、列永远沿 x 排。Chrome 对 `table-grid-paint-vlr-rtl`（body `vertical-lr` + `direction: rtl`，`.multicol { inline-size:400; block-size:100; columns:4; gap:0 }`）的探针（`build/_probe_vtable.dom`）给出该片的轴：

- **块轴 = 物理 x，自左向右**（`vertical-lr` 定块轴，`direction` 只反转 inline）；**inline 轴 = 物理 y，自下而上**（rtl）→ 栏是横条，第一栏在最下面：表盒 4 个片段 `0,380 / 0,280 / 0,180 / 0,80`，各 `100x100`。
- 上 caption（`block-size:140`）沿块轴占 140：片段 `[0,380 100x100]` + `[0,280 40x100]`——即块轴填满第一栏后在第二栏从 x=0 续 40。
- 行（唯一 td `block-size:225`）沿块轴 50 + 100 + 75 = 225：`[50,290 50x80] | [0,190 100x80] | [0,90 75x80]`；**行的 inline 延伸 80 = 栏的 inline 延伸 100 减去表自身 10px 边框的两半**（表 `inline-size:100%`）。
- 下 caption（15）在第四栏 `x=85..100`：75（行）+ **10（表块轴末端的那条边框）** + 15 ✓ → 表盒的块轴内缩要按逻辑轴向作用在 x 上。
- 对照 ref：同一 4 栏容器里三个普通块（140 / 245 = 225+2×10 / 15）总和 400 正好铺满 4 栏，与上面的分片一致——ref 不需要任何表语义，只需引擎把轴的分配做对。

同一次探针还确认 `at-boundary`（§9.80 之后仍差 2540）的表结构：片段 `21,21 92x100 | 123,21 … | 225,21 … | 327,21 … | 429,21 92x85`；第一栏只有 caption（150 切 100+50），行 0 落在第二栏 `y=76`（在续来的 50 之后）；行 1 起新栏 `232.5,21 77x100`——左线是 15px 黑（整线取最宽、单元格 rect 内缩 7.5 = 15/2），而 2540 的残差签名 `225,21` test lime / ref black 正是**片段起始处的角**：竖线（黑 15）应盖住那条被切开的横线（lime 半线）的带，即片段第一行的侧带不得上伸，且角要按较宽的线让位。

### 9.82 嵌套 fragmentainer 那三对：外层栏数被“子项本身是多栏”这条否决挡住

`repeated-section/variable-fragmentainer-size-001`（2400）、`-002`（800）、`balanced-inner-multicol`（700）三对同源。Hutu 今天的树（`build/_lookvf1.txt`）里外层 `columns:3; height:100; column-fill:auto` 根本没分栏——唯一的子项是 `columns:1; height:250` 的嵌套多栏，`ColumnsOf` 里“多栏子项要在自己的栏里再平衡，外层先退回普通堆叠”那一句直接 `return false`，于是内层被铺成 300 宽一整块（表 20x170 只画一份）。

Chrome 的片段（`build/_probe_vf1.dom`、`build/_probe_vf2.dom`，`getClientRects`）：

- **001**：内层 3 片 `100x100 | 100x100 | 100x50`——它自己声明的 250 被外层栏**按每栏剩余高度切开**（100+100+50）；表随内层切片一起分片，**表头在每片顶部重复、表脚在每片底部重复**：`head` 3 个矩形、`foot` 3 个矩形、`body` 60/60/10 ✓（每片 = 20 + 剩余 + 20；最后一片 50 = 20+10+20）。
- **002**：外层第一栏先被 50 高的空白占掉，于是内层 3 片是 `50 | 100 | 100`；表片 = 50（表头 + body 30）/ 100（body 100）/ 20（只有表脚）。这里 `head` **只有 1 个矩形**、`foot` 只有 1 个（在第三片）——即第二片（纯 body 延续）**没有**重复表头。两对的差别只在“某片里除了 body 之外还有没有别的表内容”，重复段的落点规则要按这个口径写，不能照 §9.55 那句“每个后续片段顶部都放表头”硬推。

做法已试（`build/_nested-multicol.patch`，20 行，删掉 `ColumnsOf` 那条否决后全量跑）：外层确实分栏了，但**只这一半不够**——`CutsAsBox`、`DividesAcross`、`DividesUnits`、`AddBoxCut` 四处各自把“多栏子项”挡在门外，内层还是不被切开。实测：`variable-fragmentainer-size-001` 2400 不动、`-002` **800 → 1600（更差）**、`balanced-inner-multicol` 700 不动，而绿着的 `wpt/css-multicol/multicol-rule-nested-balancing-003` 变成 115164（嵌套平衡容器的栏规则整个散架），失败名集 2008 → 2009 ⇒ 按门禁回退。剩下的另一半写清楚才动：内层作为子项要被外层切成 100/100/50 这样的片，每片把**自己的房间**当 fragmentainer（`FragmentSlice.Offset/Height` 已有），且嵌套的**平衡**容器仍走 §9.5x 的平衡规则（003 就是被这条绊住的）。

`widows-orphans-021/022`（6250 / 8750）同源但更难：Chrome 里绿色 div 被切成两片、**每片都撑满整栏**（`g [8,50 50x100 | 58,50 50x100]`，`build/_probe_wo21.dom`），浮动块落到第二片的第二行（`f [58,75 50x25]`）。Hutu 今天整块 125 高只待在第一栏。要做的是“**带背景的普通块**也按子项边界递归切”，而 `IsBareWrapper`/`IsOversetWrapper` 都明确拒绝有背景色、有浮动子的盒（`BackgroundColor is { A: > 0 }`、`IsFloat(grandchild)`）——也就是 §9.34 那次“非裁剪盒的内容切片 +7 净亏、浮动散架”缺的正是**片段里的浮动重摆**那一半。

### 9.83 `at-boundary` 的角要按边算，且切处的横线整条留在前一片

在已落地的侧带模型上试了“**片段起始处竖线吃掉角**”：`FragmentSlice.Offset/Height` 在整盒切片那处（BlockLayout `slice: new FragmentSlice(...)`，`arranged.FragmentClip = held;` 之前）填上 `portion.Offset/portion.Height`，`TableBuild.FragmentTop = borderBox.Y + slice.Offset`，`ArrangeCell` 里 `rect.Y ≈ FragmentTop` 时 `overhang = 0`（今天中段是 `+top`、首行是 `-top`）。实测 `table-collapsed-borders-paint-at-boundary` **2540 → 2415**：左角对了（`225,21` 的 15px 黑线角 ✓），但**右角反了**——`302,21` test 画 `#000000`、ref 画 `#00ff00`：那里竖线 10、横线 10 **平手，按 §17.6.2.1 横线（上方）赢**，而左边是 15 > 10 才该竖线赢。`table-collapsed-borders-paint-htb-ltr` 仍绿（顺带量清：先试的“带 = 单元格自己的盒、只在外沿外扩”变体把它打到 **200**，首差 `329,41` test 蓝 / ref 黑 ⇒ 中段必须继续 `+top/+bottom` 铺满共享线，回退）。不翻对 ⇒ 按规则回退，改动存 `build/_atboundary-corner.patch`（66 行）。

剩下的两半写死在这里：

1. **角按边判**：左右两侧各自比较**整线宽度**（`horizontal[row].Width` 与 `vertical[col].Width`，不是内半），宽者得角、平手归横线；因此侧带要拆成 `LeftBand`/`RightBand` 两个矩形，`Painter.AddBorder` 相应发三条命令（上下环 + 左带 + 右带）。
2. **切处的横线归属**：残差的主体不是角，是每片顶端那 5px 一行：`y=21..25` 上 `x=327..341`（test 蓝、ref 白）、`429..431`、`519..520`。ref 里下一段 div 写的是 `border-top: 0`（`table-collapsed-borders-paint-at-boundary.tentative-ref.html`），即被切那一刀的横线**整条归前一片**，后一片连角都不画；这与 `table-border-008` 悬着的“哪个片段在切处拥有边框”是同一半，做的时候两处一起定。

竖排那两对（`-vlr-rtl` 19380、`-vrl-ltr` 20350）在本次试验中一字未动 ⇒ 仍等 §9.81 的轴向那半。

### 9.84 `flex-container-fragmentation-006` 闭合：声明了高度的平衡容器也切，且超高 margin 自成一栏

Chrome 实测（`build/_probe_fc6.dom`）：外层 `columns:4; width:100; height:100; margin-left:-25px; gap:0`（每栏 25 宽），子项是 `display:flex; height:400; margin-top:200` 的空盒 —— `.multicol` 在 `(-17,50) 100x100`，flex 的 `getClientRects()` = **四片** `(8,50) (33,50) (58,50) (83,50)` 各 `25x100` ✓：200 的 margin 吃掉第 1 栏（剩下的 100 随断栏丢掉），盒从第 2 栏起铺满 4 栏，**第 5 栏是超出 `column-count` 长出来的**（ref 就是一个 8..108 的绿方块 ✓✓ 两侧对齐 ✓）。

两条规则，一条不够：

1. **切不只属于 fill 容器**：`AddBoxCut` 原本要 `box.Style.Multi.Fills`；`column-fill: normal`（默认）但**声明了 fragmentainer 高度**（`height:100` / `max-height`）的容器同样有定房间可切 ⇒ 参数改名 `cutsAtRoom = Fills || !Height.IsAuto || MaxHeight.HasValue`（两个调用点都改）。只加这一条：12800 → **10825**（开始切了，但片段晚了两栏）。**这条不能宽到整个容器**：第一次全量门禁把绿着的 `wpt/css-overflow/overflow-clip-margin-mul-column-border-box`（3680）与 `-content-box`（4790）打红——那是"平衡容器 + 声明高度 + 有内容的裁剪盒"，正是 §9.6x 里"cutting a balance that states its height"同一对。收窄成 `cutsHere = fills || (roomStated && child.Children.Count == 0)`（`roomStated` 只回答"房间定没定"，只有**真空盒**才准在平衡容器里被切）⇒ 两对回到 the two agree ✓ 006 仍 0 ✓ 单测 2180 ✓。
2. **超高 margin 自成一栏**：首片段前的 margin 若一栏装不下，它**花掉自己那一栏**，盒从下一栏整块开始。试过 `BreakValue.Column` 强制断栏 ⇒ 无效：`ChildSlicer.Fitting` 的强制断栏要看 `i > start`，而这是容器**唯一的子项**，首片段永远在 `start` 处 ⇒ 10825 不变。正解：在 `AddBoxCut` 的循环里，当 `first && margin.Top >= columnHeight - clonedInsets` 时**先塞一个零高度的片段**带 `Margin.Top = columnHeight - clonedInsets`、`FragmentChild.Height = columnHeight`、带 `before`/`startInset`，随后的内容片段 `headMargin = 0`、房间整栏 ⇒ 10825 → 3300 → **0**（中间一次把 margin 归零但没花栏，导致整体左移一栏：差 800 + 2500 = 3300 ✓ 正是那一栏的位移）。

margin 片段是**纯占位**：零高度、不带 `before`、`startInset` 为 0 —— 盒的上边缘仍归第一个有内容的片段（CSS Break 3 §4.7：切出来的边属于显示该边的那一片）。这一点是在跑门禁前改定的：先前版本让占位片段带上 `before` 与顶边，会把盒的顶边画进 margin 那一栏；语料里没有“空盒有边框 + 超高 margin”的对，改法在 006 上同样 0，且单测 2180 不变。

顺带把 `overflowing-block-003`（8300）的 Chrome 地面真值量下来存着（`build/_probe_ob3.dom`，两个用例各一对片段）：`.outer{height:0}` 装 100 高绿盒的两栏用例，outer 两片都是 `50x0`、inner 两片 `50x50` ⇒ 穿过头顶为 0 的父盒去切内容，这条走 `DividesUnits`（`fills` 为真、父盒无浮动子项 ⇒ 该切就已成立）；另一用例 `.outer{margin-right:25}` 里 `float:left` 的 50x50 绿块**在两栏各画一次**（`f1 [21,53 50x50 | 96,53 50x50]`）⇒ 差的是"放不下的浮动在后续 fragmentainer 里重摆"那一半，与 §9.34/§9.84 的"片段里的浮动重摆"同一个缺口。

全量门禁（基线 = `5859926b` 那份报告 `build/_score-cl2.md`，名单 `/tmp/mine-cl.txt` 2008）：单测 **2180** 绿；打印 **189/198**、九个失败名逐字不变；屏幕 **2008 → 2007**，`--- regressed ---` 空，`--- fixed ---` = `wpt/css-break/flexbox/flex-container-fragmentation-006`，`wpt/css-break` **37 → 36**。报告存 `build/_score-fc6b.md`，切片补丁 `build/_fc6-margin-piece.patch`。

### 9.85 折叠边框又量否两条：片段起始的侧带上伸、以及逐段取宽

`at-boundary` 2540 的真相不是"角"，也不是"宽度分段"，两条都已实量并回退（HEAD 仍是 `11b2bf16` 的形状）：

1. **片段起始处让侧带上伸到片段顶**（`build/_atboundary-corner.patch` 66 行）：2540 → **2415**，左角 `225,21` 对了，右角 `302,21` 反而把黑画进了本该 lime 的地方 ⇒ 不翻对，回退。规则要按边比较整线宽（15 > 10 竖线赢、10 = 10 平手横线赢），侧带得拆成左右两条矩形。
2. **纵线按片段取宽**（`build/_collapsed-segment-widths.patch` 92 行：`TableCellInfo.Sides` + `LayoutBox.SideWidths` + `Painter` 侧带用它，几何仍按整线最宽分列）：`at-boundary` 2540 → **2490**（几乎没动）、`collapsed-border-large-rowgroup-002` **904 → 911（更差）**、`htb-ltr`/`large-rowgroup-001` 仍绿、`border-collapse-006` 17500 与 `subpixel-collapsed-borders-003` 1392 不变 ⇒ 不翻对且两对更远，回退。

残差的形状（`build/_lookab3.txt` 的 paint 比对）是**每条片段左右两端各 2–3px 的细条，沿约 190 行**：`x=315..316 / 327..329 / 302..304 / 429..431 / 506..508 / 519..520`（test 画线、ref 画白）加 lime 一侧的 `123..125 / 136..137 / 200..202 / 213..214`。Hutu 的命令是 `border 21,226 92x100 w:0/15/0/15`（侧带 = 单元格盒 92 宽、两端各 15）加 `border 21,221 92x100 w:5/0/5/0`（上下环），而 Chrome 探针给的是 `t [225,21 92x100]`、`r1 [232.5,21 77x100]` —— 单元盒内缩 7.5，竖线整条 15 骑在网格线上 ⇒ 两者**表面上等宽**，差别只在**片段边界那几像素归谁**：需要 Chrome 实际**画出来**的像素扫描才定得下来（`chrome --headless=new --screenshot=… --window-size=…` + Python 按行扫色带；§9.6x 给 `table-border-008` 定案时用的就是这个办法，别再靠像素推断）。

同一族剩下的竖排两对（`-vlr-rtl` 19380、`-vrl-ltr` 20350）在这两次试验里数字一字未动 ⇒ 仍等 §9.81 的轴向那半。

### 9.86 用 Chrome 截图扫色带定下折叠边框的完整模型（四条一起做才闭合）

像素扫描这条路子终于把 `at-boundary` 说清楚了：`chrome --headless=new --screenshot=<绝对路径> --window-size=800,600 --hide-scrollbars file:///...`（相对路径会被 Chrome 拒写 ✗）+ PIL 按行扫（`build/_ab_test.png`、`build/_ab_ref.png` ✓ 两页逐行完全一致 ⇒ ref 诚实 ✓✓）：

- `y=60`（行 1 所在的片段）：`225..239 黑(15)` `305..314 黑(10)` ⇒ **纵线按它自己那一段的宽度画**，且**骑在网格线上**（左右各半）。左线 15 中心 232.5、右线 10 中心 309.5 ⇒ Chrome 的 `r1` rect `232.5,21 77x100` = 表盒 92 减左右各 7.5 ✓✓ 即**inline 方向也有外半**，外半在表盒里、不在单元格里。
- `y=22`（片段顶上那 5 行）：`225..239 黑` `240..304 lime` `305..314 黑` ⇒ **横线被斜接**：从左边线的外沿结束处画到右边线的外沿之前，斜进量 = **该段线宽的一半**（不是几何半宽、也不是整段宽）。

四条规则合起来才是完整模型（先实量了前两条的组合：`at-boundary` 2540 → **6415（更差）**、`htb-ltr` 0 → **4700**、`large-rowgroup-001` 绿 → 16、`subpixel-003` 1392 → 1704，只有 `border-collapse-006` **17500 → 15000** 在变好 ⇒ 已回退，改动存 `build/_collapsed-inline-halves.patch` 200 行 + `build/_collapsed-segment-widths.patch` 92 行）：

1. **几何**：折叠表盒的 `InsetsH` 也含左右外半（`build.OuterLeft/OuterRight = vertical[0].Width*0.5 / vertical[cols].Width*0.5`，`left/right = vertical[线].Width*0.5`）——这条把 006 从 17500 推到 15000，说明 §9.79 记的"006 咬住整条内半"并不是反证 ✓ 值得单独立片。
2. **表盒只画上下外半**：左右的**外半要由每一行的侧带向外伸**（外半宽度逐行不同：行 1 是 5、行 2 是 7.5）⇒ 侧带必须拆成 `LeftBand`/`RightBand` 两个矩形、中心贴在格子线上、宽取该段宽（今天只有一条 `SideBand`，宽度取几何 ⇒ 外半永远按整线最宽画 ✗）。
3. **上下环斜接**：环的矩形向内收 `sides.Left/2`、`sides.Right/2`（`sides` = 该段宽，见 2）。
4. **上下线宽是否也按列分段**还没量（`htb-ltr` 的 4700 全在 `226,21` 一带 ⇒ 多半与 2/3 未做全同源）；做 2+3 之后要重看。

下一步照这个顺序做：先补 2 与 3（画侧不动几何），`htb-ltr` 应回 0；再看 4。截图扫带这条手段以后优先于 paint 清单猜：两页 PNG 逐行比对能一次区分"谁的哪条线宽了多少"。

### 9.87 折叠边框：三种组合都量过了，最好的把 `at-boundary` 打到 110 但 `htb-ltr` 退 200

按 §9.86 的像素模型做了三轮实测（HEAD 未动，三个版本都留在 `build/`）：

| 版本 | 内容 | `at-boundary` | `htb-ltr` | 其它 |
|---|---|---|---|---|
| A（`_collapsed-inline-halves.patch` 200 行）| 规则 1（inline 外半进表盒几何）+ 表盒照旧画四条外半 + 逐段宽（`SideWidths`）+ 上下环向内斜接 `border.Left/Right` | **2540 → 110** | 0 → **200** ✗ | `large-rowgroup-001` 绿 ✓、`006` 17500 ✓（不变）、`subpixel-003` 1392 ✓、`rowgroup-002` 904 → 929 |
| B（在 A 上把表盒的左右外半**不画**，改由单元侧带向外跨）| 同 A + 侧带向外 `sides/2` + 环斜接 `sides/2` | 110 → 4735 ✗ | 200 → 5200 ✗ | — |
| C（在 B 上把向外跨做成"仅当该段比上下两条横线宽"）| 条件 straddle，环不收 | 4735 ✗ | 5200 ✗（`226,21` 该处变白 ⇒ 表盒左右外半必须画）| 补丁 `_collapsed-inline-halves-v2.patch` 245 行 |

像素给出的判定（`build/_ht_test.png` / `_ht_ref.png` 逐行扫，两页一致）：`htb-ltr` 在 `y=21` 处黑线是 **226..235（整条 10 全在盒内）** ⇒ **表盒必须画 inline 的外半**（B/C 把它取消 ⇒ `226,21` 就白了 ✗✗）；而在 `y=36..40` 黑横线跨 **329..420 = 覆盖整段竖线** ⇒ **等宽时横线吃掉角**、不收斜边 ✓；`at-boundary` 的 `y=22` 黑线 **225..239（15）**、lime 横线 **240..304** ⇒ **竖线更宽时才收斜边** ✓。A 版本已经含这三条里的"收 `border.Left`"（=几何内半，不是段宽一半）⇒ 对 `at-boundary` 正好、对 `htb-ltr` 多收了 5px（那 200 就是 `226..232` 与 `329..333` 这类等宽角 ✗）。

**差最后一步**：斜接量要按边取 `min(border.Left, sides.Left > 该处横线整宽 ? sides.Left/2 : 0)` —— 也就是"等宽不收、宽者收自己的一半"，而外半始终由表盒画（B/C 的教训）。

又量了两版（都不翻对 ⇒ 已回退，代码在 `build/_collapsed-side-bands-conditional.patch` 188 行）：

- **D1 逐边侧带 + 条件角**：`SideBand` 拆成 `LeftBand`/`RightBand`（每条带自己一个矩形、宽 = 该段宽，绘制顺序 = 先上下环、再左、再右 ⇒ 谁后画谁得角），顶沿 `firstRow ? -topHalf : (seg > topWhole ? 0 : topHalf)`。实测 `at-boundary` **2540 → 2340**、`htb-ltr` **0** ✓、`006` 17500 ✓、`large-rowgroup-001` 绿 ✓、`subpixel-00{1,2,3}` 不变 ✓、`rowgroup-002` 904 → 919（仍红，只涨像素）。⇒ 无新增失败名但也**没翻对** ⇒ 按门禁规则回退。
- **D2 无条件收角**（`top = firstRow ? -topHalf : 0`）：`htb-ltr` 退 **200**（首差 `329,41` test 蓝 / ref 黑 ⇒ 那里的角该归横线）✗ 直接否。

于是"等宽时谁得角"在两页里答案相反，宽度不是判据：**at-boundary** 在 `y=21` 的黑色右边线（10）压住同宽的 lime 横线（黑 305..314、lime 只到 304 ✓ 竖线赢），而 **htb-ltr** 的 `y=36..40` 黑横线整跨 329..420（横线赢）。两者差在**冲突序**：at-boundary 那处横线是"行 0 的 lime 底边"与竖线"行 1 的黑右边"，宽与式样都同、来源同为单元格 ⇒ 按 §17.6.2.1 的余下一条定胜负（下一步该把这两处的四个候选边框逐个抄出来比对，而不是再试宽度规则）；htb-ltr 那处横线是**上一行的整条底边**（它在 ref 里就是 10 宽整条归上行 ✓）。也就是说候选"是否属于上一行"可能是真正的判据 —— 待量。

### 9.88 骑跨式侧带把 `at-boundary` 打到 1365，只差"横线段的胜者属于哪一行"

E = D1（逐边侧带）+ inline 外半几何（`OuterLeft/OuterRight` 进 `build.Border` 与 `InsetsH`，单元盒只留内半）+ 侧带矩形骑跨网格线（`X = rect.X ± width/2`）+ 上下环**不再收边**（靠"环先画、竖带后画"的次序定角）+ 顶沿条件 `firstRow ? -topHalf : (seg > topWhole ? 0 : topHalf)`。

实测（`build/_collapsed-straddle-bands-E.patch` 238 行，已回退）：`at-boundary` **2540 → 1365**（D1 是 2340、A 是 110 但 A 破两绿）；`large-rowgroup-001` **回到绿** ✓（A 曾 16 px，条件斜接把它消掉了）；`border-collapse-006` 17500 ✓、`subpixel-001/002` ✓ 不变；`rowgroup-002` 904 → 1025。但 `htb-ltr` **0 → 100** ✗（首差 `329,41` test lime / ref black）⇒ 破绿 pair，按门禁回退。把顶沿改成无条件 0（骑跨版 D2）：`at-boundary` 1315、`htb-ltr` 200 ✗ ⇒ 条件版更好，已回退到条件版再整体回退。

**剩下的唯一未知已排除两个假设**：`329,41` 那格不是几何/角的问题。E 的 100 px 残差里，Hutu 在 `x=329..338`（该片段竖线的位置）画 **蓝**，Chrome 画 **黑** ⇒ 同一条纵向线在**这一行**的胜者候选选错了（`outer[]` 与 `cellLeft/cellRight` 的次序，或行/列候选与单元格候选的优先序），与 §9.79 那次"逐段定色"的候选序同一族。又试了"横线段的胜者若属本行则横线吃角"（`cell.TopIsOwn = StandsFor(cell.Top, horizontal[Row])` ⇒ `BandOf` 顶沿改判据，补丁 `build/_collapsed-E-topisown.patch` 266 行）：`at-boundary` 仍 1365、`htb-ltr` 反而 100 → **250** ✗ ⇒ 角假设不成立，已回退。

于是这条路剩下的活分两件、都可单测：(1) **竖线段候选序**——把 htb-ltr 第 2 行左线（Chrome 黑 10 / Hutu 蓝 10）的四个候选抄出来对一遍；(2) 之后再回到 E（`build/_collapsed-straddle-bands-E.patch` 238 行 = 目前最好的几何基：at-boundary 2540 → **1365**、`large-rowgroup-001` 绿、006/subpixel 不变，仅 htb-ltr 因 (1) 剩 100 px）。(1) 一旦修好，E 就可能同时翻 `at-boundary` 并保持全绿；三个几何补丁都在 `build/`：`_collapsed-inline-halves.patch`（200）、`_collapsed-side-bands-conditional.patch`（188）、`_collapsed-straddle-bands-E.patch`（238）。

### 9.60 两次量过又回退：竖排阴影带与折叠边框分段

**竖排阴影带（`box-shadow-003`）**。两处改动合起来能让它转绿（10860 → 0）：①`Painter.BandOf` 在 `BlockIsRightToLeft` 的块轴上把“起点/终点”对调（起点在高坐标一侧），10860 → 7400；②`PositionedLayout.Horizontal` 对“无块向 inset 的竖排绝对盒”改为 `x = staticX - width - margin.Right`，7400 → 0。Chrome 探针证实②的规则本身正确（vertical-rl 容器内三个无块向 inset 的绝对盒都落在 `x=60`，即右缘起向左生长）。但②同时弄红 `wpt-variants/text-spacing-trim-colon-001-{chws,halt}-vrl`（字形墨迹移 2–3px）与 `css-flexbox/alignment/flex-align-baseline-column-vert-rl-rtl-wrap-reverse`（绝对项移到容器左侧 100px）：`ArrangeFlow`、flex/grid 的静态位置调用方给出的是**不同含义的点**（块起点 vs 内容左上角），`Horizontal` 分不清；而且 003 的正解是“流坐标 OOF 分片”（ref 页与 test 页的阴影带要在同一坐标系里对齐），不是挪 ref。整体回退，等 OOF 流坐标模型一起做。

**折叠边框分段（`table-collapsed-borders-paint-htb-ltr` / `-at-boundary`）**。`TableFormattingContext.Grid` 的 `vertical[]`/`horizontal[]` 每条网格线只保留一个解析结果，纵向线的颜色因此被第一行的单元格“定调”。按 CSS 2.1 §17.6.2 改为**逐段解析**：新增 `outerVertical[]` 只累积行/行组/列/列组/表格的候选；每个单元格的左右颜色与线型由 `Resolve(自己的一侧, 同行相邻单元格相对的一侧, outerVertical[线])` 得出；宽度仍取整条线（不动列几何）。实测 htb-ltr 8000 → 4200、at-boundary 10105 → 6600，颜色对了，但两对仍差：剩余是**行带相位差 5px**（测试黑带 31..40，ref 36..45），来自 caption/行边界处“半边框”（5+5）与 ref 假列“整边框”（10 在上）的累计差。宽度也要分段（而非整线取最宽）才可能闭合，那会动到列几何，留作下一片。


### 9.9 块的 float 摆放：两条规则都要，缺一条就不收敛

`float-009` 的 dump 显示 `A<div style="float:left">X</div>` 里我们的 `X` 落在行的**下方** `(8,58)`，而 ref 是 `AX` 并排（`float-009-ref.html` 直接写 `<div>AX</div>`）。做了第一条规则——"float 的顶不得高于同一块容器里前一个匿名行盒的顶"（CSS 2.1 §9.5.1，两个 pass 同步、只在 `child.IsAnonymous` 时生效）——之后 `X` 到了行的顶 `(8,8)`，但 x 仍是内容左边缘，与 `A` 重叠；ref 要的是 `(58,8)`，即"行内内容之后的 float 排在该行当前 inline 位置"。第一条规则单独落地时 `float-009` 12500 → **7500** px，`borders-001` 保持绿（它的 ref 用 `float:left`，必须靠"只认匿名块"救回），但 `CSS2/abspos/static-inside-float-inside-inline` 与 `css-break/flexbox/flex-fragmented-with-float-descendant-001` 变红，净 +2，整片回滚。下一步：把"float 排在该行当前 inline 位置（行盒已用宽度）"补上，两条一起量。

### 9.10 切片 F：竖排多栏（栏沿 inline 轴、块轴反向）

`ColumnsOf` 过去对 `box.Flow.IsVertical` 直接返回 false，竖排多栏一律退化成普通堆叠。现在按**行内尺寸**（竖排是高度）分栏，栏内子盒沿块轴（竖排是宽度）堆叠；切片的块向边在**左右**，vertical-rl 的块轴从右往左、片也自右向左排。关键的一行是 `AddBoxCut` 里遗留的 `size.Height <= columnHeight` 门槛（竖排子盒的 `size.Height` 恰等于栏高，导致永远不切），改为按块向 extent 判断后立刻生效。

范围线：竖排容器**含 float（含嵌套）或 `column-height` 换行**时仍走普通堆叠——这两条 walk 还没写；`ColumnsOf` 里直接挡住。

实测：屏幕全量 **2095 → 2093（新增 0）**；`borders-006`、`borders-007` 转绿；单测 2180；打印 189/198 不变。竖排阴影两对仍差：`box-shadow-004` 1132（AA 软边）、`box-shadow-003` 12157（阴影带在反向块轴上的位置待查）。
### 9.11 块的 float 摆放：第一条规则已量、第二条缺行宽 API

`float-009` 的 `A<div style="float:left">X</div>` 要求 `X` 与 `A` 并排（ref 直接写 `<div>AX</div>`）。已实现并量过第一条规则——"float 的顶不得高于同一块容器里前一个**匿名**行盒的顶"（CSS 2.1 §9.5.1）：`X` 从行下方 `(8,58)` 升到行的顶 `(8,8)`，像素差 12500 → 7500，`borders-001`（ref 也用 `float:left`）保持绿；但 x 仍是内容左边缘，压住 `A`，需要第二条规则"float 排在该行**当前** inline 位置"。单独落地第一条会红两对（`CSS2/abspos/static-inside-float-inside-outline`、`css-break/flexbox/flex-fragmented-with-float-descendant-001`），故未提交。

第二条缺的是**行已用内联宽度**：`InlineLayout` 只暴露 `LineHeights`，没有 `LineWidths`；measure 与 arrange 必须拿到同一个数，否则容器高度会漂。已先把 `FloatTracks.Place` 加上 `inlineOffset`（默认 0，行为不变，56 个 float 单测全过），供下一步接线。

**接线量过一轮（未提交）**：`LineWidths` 已落地（`b2ca452b`），两条规则接上后 `float-009` **转绿**、`borders-001` 保持绿；但三对变红，逐一收敛：

- `css-break/flexbox/flex-fragmented-with-float-descendant-001`：把规则限定在"自身不是格式化上下文根"的盒子里（flex 项、裁剪盒、定位盒各有自己的排布）后**恢复**；
- `CSS2/pagination/float-page-break-inside-avoid-7-print`：同一限定后也恢复（打印对，需再验）；
- `CSS2/abspos/static-inside-float-inside-inline`：**仍红**——`<span line-height:200px><br>` 之后紧跟 float，`<br>` 造出的空行让 float 升到了行首（应为 `<br>` 之后 200px）。需要"前一行是否由强制换行造成"这个判断（空行不应抬高 float），这条判断还没写。

measure 侧还有一处要小心：`LineWidths` 会重新规划行，若对每个行盒都调用，会二次规划同一内容并扰动 float 记账；正确做法是**推迟到真的有 float 要放时**再算（按 `lined` 记住前一个行盒）。

**第二轮接线（仍红，已回滚）**：按上一条把 `LineWidths` 推迟到 float 处再算，并把规则只放在 **arrange** 侧（measure 侧保持原样）；再依次加三条限定——①只对"自身不是格式化上下文根"的盒子、②行必须**部分填满**（整行或 `<br>` 造成的空行都不把 float 抬高，本来也放不下）、③容器不能是 `IsInlineOuter`（行内盒的行由 inline 排布负责）。结果 `float-009`、`borders-001`、`flex-fragmented-with-float-descendant-001` 全绿，但 `CSS2/abspos/static-inside-float-inside-inline` **仍然红**（float 升到 `<br>` 行首，应在其后 200px）；HEAD 该对是绿的，说明问题出在这轮改动里，而三条限定都排除不了它。下次应从**打印 `lineTop`/`reach` 的实际取值**入手（该页 dump：`block span` 下 `~block` 与 float 并列，float 落点与 `~block` 同 y），确认是谁把 float 抬上去的。



### 9.8 按片裁内容：做出来了，但被 float 那一侧挡住

实现过的版本（三处）：`BlockLayout.Arrange` 里切片盒子的内容按**整盒**（`FragmentSlice.Whole` + 盒子自己的 insets）排一次；`LayoutEngine.ResolveDecorations` 给带内容的切片盒子打开 `ClipsOverflow`，于是每片只画自己那一段；`AddBoxCut` 允许切"断点在行里"的盒子（`HoldsBlockChildren` 为假，float 不算断点；`Height.IsAuto` 且无 float 的仍不切）。

实测：`box-decoration-break-clone-009`、`flexbox/multi-line-column-flex-fragmentation-041` 转绿，`transform-006` 从 45771 降到 **7226**（变换盒有了自己的分片，但 ref 还要求每片各自算 `transform-origin`，残差仍在）。整族 +7：`CSS2/floats-clear-multicol-001` 变红——它的 ref 页 `.container`（250 高、只有空白文本子节点）因此被切，test 页同一个 `.container` 里是两个 float 加一个 `br`、高度由 float 撑出（auto），后一道门槛把它留在不切，两侧发散；再把 float 从"断点"里排除、给 float 撑高的盒子开口，仍差 9510 px（`.clear` 落点与 aqua 位置不对）。这条要靠 **block 内 float 跨栏分片**才解得掉，与 `float-009`、`float-017`、`widows-orphans-021/022`、`floats-clear-multicol-001` 是同一块。整片回滚。

### 9.89 段的角色看 display，不看标签名（mixed-order 899 → 87）

`table/sections-and-captions-mixed-order` 的九个 `<div style="display:table-...">` 里有两个 footer：Chrome 把"7"排在"8..13"之前，分行与不分行两种口径都量过（`build/_mo1.html`：单栏不分行时 1,2,3,4,5-6,7,8-13,14,15-20）。理由在 CSS Tables 3 §3.1：`table-header-group` 只有**第一个**当真表头（提到所有行之前），`table-footer-group` 只有**第一个**当真表尾（放到所有行之后），后面的那些按 `table-row-group` 处理、留在原处——所以 DOM 里第二个 footer"7"只是普通行组，跟着文档序走。

Hutu 的排序在 `TableFormattingContext.CollectRows` 里读的是**标签名**（`thead`/`tfoot`），`display:table-header-group` 的 `div` 因此从不被提起；同一个文件里已有按 computed display 判角色的 `RoleOf`（`RepeatSections` 用它），只有排序没用。改成 `RoleOf` 后 899 → 87。

**同一改动逼出的第二条规则**：重排中间那一遍原先写 `!ReferenceEquals(entry.Group, head) && !ReferenceEquals(entry.Group, foot)`，没先问 `foot` 在不在——于是"有表头、无表尾"的表里，`Group` 为 null 的行（裸 `tr`、围绕散落内容生成的匿名行）与 null 相等，整条被丢掉。实测：`table/repeated-section/text-in-nested-multicol` 由绿转红 156 px，dump 里表格从 16x220 塌成 16x20，200 高那一行不见了。补上两个守卫后恢复，与上一条同片落地。

残差 87 px 全在 col3/col4 首行的"11"与"16"：Hutu 量得 15.41 宽（两个数字各 7.705），Chrome 是 16.0——数字之间的字距（pair kerning）差别，属"不追亚像素"那一档，不动。

范围线：打印口径里 `PageSequence` 三处仍用 `LocalName` 判 thead/tfoot（1513、1518、1620），display 写的分片在"按页重复"那一侧还不认；那条要连带"只有第一个才重复"的语义，打印语料没有覆盖，单独一刀。

门禁：屏幕全量两侧同为 1376 个失败名（**0 新增**），`mixed-order` 899 → 87；单测 2180；打印 189/198，九条同名。

### 9.90 打印口径也按 display 认段（同一条规则的第二处）

`PageSequence` 用 `Element.LocalName` 判 thead/tfoot（原 1513、1515、1620 三处）决定"哪一段在每页顶/底重复"。CSS Tables 3 §3.1 同一条规则：角色看 computed display，而且**只有第一个** header 组、**只有第一个** footer 组保得住这个角色，后面的按普通行组走。

- 规则收一处：`TableStyle.SectionRole(display)`，`TableFormattingContext.RoleOf` 改成调它；
- `CollectTable` 只把第一个角色组记成 header/footer（原先用 `Union` 把多个 thead 并成一条带，`Union` 随之无用，删掉），第二个 thead/tfoot 落回 body 分支、按可行的行去切；
- footer"整段不切"那条判断改成 `CollectTable` 传下来的 `repeatsAsFooter`，不再读标签名。

诚实记一条：这一处在**任何语料上都画不出差别**——打印 189/198 九条同名、屏幕全量 1932 条 finding 与 `cf223564` 逐名相同（0 新增、0 转绿）、单测 2180。想做正面证明也不成立：`build/_probe_role_print.html`（`display:table-header-group` 的 div）与 `build/_probe_role_print2.html`（真 `<thead>`）在 `@page 100x100` 探针里表头都只画一次，说明探针没触到"重复带"那条分支，因此这一步只能按"同一条规则不留半个"落地，靠三道门禁保证不回归。

### 9.91 让栏切开"带浮动的普通块"：022 转绿，但两处绿对被打红，整片回滚

两条规则一起量（补丁留在 `build/_divide-and-clip-floats.patch`，112 行）：

1. `PieceBreaks` 遇到 float 子节点改为**跳过**（它不给出断点，也不再否决整段裁切），`DividesUnits` 去掉 `HoldsAnyFloat` 那条否决——栏因此可以按子项边界切开一个"里面有浮动"的普通块；
2. 切出来的片段裁剪到**栏盒**而不是片段自己的底边：新增 `HungBelow(piece)` 量该片段直接子里浮动伸出盒底多少，裁剪带向下延到 `min(栏底, held.Bottom + hang)`。

Chrome 地面真值（逐子盒着色 + PIL 逐列扫描：`build/_c021.html`、`build/_c022.html`、`shot-_c021.png`、`shot-_c022.png`）：022 的 `g` 两片是 50x100 与 50x75，浮动在绝对 58,83（第二列的 75..100）——**浮动伸出盒底但仍然画出来**，整块 100x100 全绿；021 是 col1 = d1+A+B+C=100、col2 = 末行 D(0..25) + 被推来的浮动(25..50)，且 `g` 的第二片矩形是 50x100（整列被覆盖，这条还没解释清）。顺带把"末尾 `<br>` 是否造空行"钉死（`build/_tailbr.html`：Chrome `A<br>B<br>`=40、四个 `<br>`=80、`A<br>`=20），Hutu 的盒高 150 = 50+4×25 与之一致，行数不是差异来源。

全量口径实测（合并两条那一版）：css-break 36 → **35**，`widows-orphans-022` 转绿、`widows-orphans-021` 6250 → 2500；但**两个本已绿的名字变红**——`wpt/CSS2/floats-clear/floats-clear-multicol-001`、`gecko/flexbox/pagination/flexbox-multi-row-1a`，且 `flexbox-multi-row-1b` 7250 → 32700。单测 2180 与打印 189/198（九条同名）都过。按"打红绿对就回滚"处理，`git checkout -- src/` 后重建并复核：css-break 回到 36、021=6250、022=8750。

第二版（只在 `columnBottom > held.Bottom` 时才用栏底封顶、否则纯延长，避免把越栏片段的裁剪**缩小**）只在**模块口径**里量过，模块与全量对这几对不一致，故未当作门禁；回滚后未再跑全量。

下一步的收敛点已命名：变红的三对都是"盒底以下有浮动、而 ref 要求把它剪掉"。要区分 022 与它们，候选判据是①只对**该片段的最后一块**（`portion.Last`）延长、②盒子不能是自裁的（`ClipsOverflow`）、③浮动必须是这段流自己摆下的那一支（而非更深一层）。先从 `floats-clear-multicol-001` 的 dump 读它到底多画了 9510 px 里的哪一块，再决定加哪一条。

### 9.92 浮动参与分片第二版：三条判据各自挡住一次回归，022 转绿

回滚那刀的活重新做了一次，用**模块口径当 A/B 仪器**（`HUTU_SCORE_ONLY=<族名>` 只跑 9 秒，先用 HEAD 标定：`flexbox-multi-row` 5 条、`floats-clear` 16 条、`css-break` 40 条），因此每加一条判据都能立刻知道是哪一条把哪一对打回。最终三条一起：

1. **被切那层自己的浮动不给断点也不否决裁切**（`PieceBreaks` 遇到 `IsFloat(child)` 直接 `continue`），`DividesUnits` 去掉 `HoldsAnyFloat` 的一刀否决；
2. **靠浮动撑长的盒只有一个自有单元时不切**：`HoldsAnyFloat(child, ...)` 时要求 `units.Count >= 2`（CSS Fragmentation 3 §4.2-§4.3）——这一条把 `wpt/CSS2/floats-clear/floats-clear-multicol-001`（`.container` 里两个 250 高浮动 + 一条 `<br clear=all>`，自有单元只有 1 个）从被打红救回；
3. **往下一层仍按老规矩否决**：`PieceBreaks` 的循环里遇到"子盒内部再含浮动"就 `return null`——`gecko/flexbox/pagination/flexbox-multi-row-1a` 的 **ref 页**用 `float:left/right` 成对模拟每栏，测试页本身一字未动（HEAD 与补丁态的 test PNG 差 0 px，ref PNG 差 28625 px 是这么定位到的）。

裁剪那半也收到最窄：**只有"按自有单元切开"的盒的最后一块**（`portion.Last && child.Last && child.Breaks is not null`）才把裁剪延到 `held.Bottom + HungBelow(piece)`，并以栏盒底封顶；A/B 证明单靠第 1 条不产生任何回归（multi-row 回到 5 条、1b 回到 7250），回归全部来自未加这道门槛的裁剪。

全量口径门禁：屏幕 0 新增失败名、**`wpt/css-break/widows-orphans-022` 转绿**（css-break 36 → 35）、`widows-orphans-021` 6250 → 2500；单测 2180；打印 189/198 九条同名。注释收束之后的最后一次改动只动注释、行为不变，重跑单测与三个族口径确认数字一致。

021 剩下的 2500 是"放不下的浮动被推到下一栏、后面的内容仍留在本栏"那一半（Chrome：col1 = d1+A+B+C=100，col2 = 末行 D + 被推来的浮动 25..50），仍未写。

### 9.93 混排浮动的栏：新路径按 Chrome 逐值落位，tall-float-002 转绿

`tall-float-pushed-to-next-fragmentainer-002` 那类"栏里既有浮动又有普通块"的形状，原来走的是把浮动当流内盒**逐个占高堆叠**的路子：Chrome 却让左右浮动**共用同一条带**、浮动不占流的高度、放不下时才换栏。用 DOM 探针取到 Chrome 真值（`build/_tf002.html`）：`8,50 50x100` / `98,50 10x100`（右浮动贴第二栏右边）/ `58,50 40x60`（左浮动贴第二栏左边，与右浮动同带）/ `58,110 50x40`（`clear:left` 的块落在左浮动之下、右浮动仍可与之重叠）。

新增一条自足路径（`FloatFlowColumns` 判据 + `PlaceFloatFlowColumns` 走一遍 + measure/arrange 两个包装）：
- 浮动：`tracks.Place(side,…)` 取边（左 = 栏左、右 = 栏右），带顶 = 当前流位置，**不推进流**；
- 流内盒：先 `ClearBelow(clear, flow)`；带行盒的（`BreaksAtLines`）再 `FitBelow(..., width)`——块盒可以压过浮动，行盒放不下才走到浮动底下（这条同时解释了 002 的 clear 块为什么在 60 而不是 100，以及 `widows-orphans-020` 的 `&nbsp;` 行为什么被整条挤到第二栏）；
- 谁的落点超出栏高就换栏重开一条 track（浮动"推到下一个 fragmentainer"由此自然成立）；容器高度**保持声明值**，不被内容撑大。

判据里排掉两件事，都是量出来的：①盒子里有比一栏还高的**流内**盒 —— 那种要被切开，本路径不切（否则 `fragmented-autowidth-fc-root-beside-floats` 从 34400 恶化到 89000，`float-017` 也从 663 变 653）；②竖排、`column-wrap`、未声明高度、非 `column-fill:auto` 的容器。第一版没排掉①时，`widows-orphans-020` 被 `FitBelow` 那条补上之前也打红过 1000 px。

全量口径门禁：屏幕 0 新增失败名，**`tall-float-pushed-to-next-fragmentainer-002` 转绿**、附带 `wpt/css-multicol/multicol-gap-fraction-002`（20 px）转绿，css-break 35 → **34**；单测 2180；打印 189/198 九条同名。

余下的浮动对：`widows-orphans-021` 2500（orphan 逼出的浮动推移 + Chrome 那侧第二片矩形高 100 的机制未解）、`overflowing-block-003` 8300 与 `fragmented-autowidth-fc-root-beside-floats` 34400（都要"高盒被栏切开 + 每片各自与旁边浮动求自由宽"）、`float-017` 663、`line-after-unbreakable-float-after-padding` 312。

### 9.94 装不下任何一栏的浮动续切；`overflowing-block-003` 从 8300 到 2500

混排栏那条路补了三种情形，都用 DOM 探针对着 Chrome 逐值核过（`build/_ob3.html`：`outer [21,53 50x0][96,53 50x0]`、`inner [21,53 50x50][96,53 50x50]`）：

1. **裸浮动壳提升**：一个盒子若自身无装饰、无声明尺寸、内容只有浮动，栏看到的不是它而是它里面的浮动，宽度沿用壳留给浮动的room（`overflowing-block-003` 第一个盒子 `.outer{margin-right:25}` 让 75 栏里的浮动只有 50 宽）；
2. **装得下就整块推栏、哪儿都装不下才续切**：`CutDownColumns` 只在浮动高度超过一整栏时才按浮动自己的行边界切成一片一栏；100 高的浮动在剩 20 的栏里仍是整块推到下一栏（保住 `tall-float-pushed-to-next-fragmentainer-002`），100 高的浮动落在 50 高的栏里就分成 50+50（`overflowing-block-003` 第一个盒子由此转绿）；
3. **片段裁剪**：混排路径的每一片现在都带 `FragmentSlice` + `FragmentClip`，盒子铺在整盒坐标上、只显示本栏那一段。

三道门禁：屏幕全量 1929 条两侧同名、**0 新增失败名、0 转绿**（`overflowing-block-003` 8300 → 5000），单测 2180，打印 189/198 九条同名；`floats-clear`、`flexbox-multi-row` 模块口径与 HEAD 一致。

`overflowing-block-003` 还剩 2500，全是第二个盒子：`.outer{height:0}` 套一个 100 高的普通块——Chrome 把**这个盒**也切成 50+50。试过在常规路径的填充循环里加一条"涂得比栏底还低就得切"（`used + Painted > limit` 而 `used + height <= limit` 时不整块放下），但它会顺手把 `flexbox/flex-item-content-overflow-001a/001b/002a/002b`（各 5300）打红：那四对的栏内子盒正是**由弹性容器展开来的弹性项**（`MulticolChildren` 的 `flexItems` 展开），弹性项的溢出内容按 Chrome 是不切的。加 `Box.Kind == Block` / `!Relays` 两道限定仍挡不住，因为弹性项本身就是 Block。下次要么给展开来的弹性项一个显式标记再排除，要么把这条断只放在"盒自身声明高度为 0 且超出部分来自声明了高度的普通子块"上——并且必须连 `flex-item-content-overflow-*` 一起量。

### 9.95 裸壳的水平 margin 不再挡住展开：`overflowing-block-003` 转绿

上一刀留下 2500 px，全在第二个盒子。两形状对照（`build/_shapeA.html` / `_shapeB.html`）说明差别只有一条：A（`.clip{height:2}` 套 150 高的块，栏高 100）被 `SpliceOverset` 展开，内容按栏切；B（`.outer{margin-right:25px; height:0}` 套 100 高的块，栏高 50）不展开——因为 `IsOversetWrapper` 与 `IsBareWrapper` 都把 `margin.Horizontal != 0` 当作"这壳有自己的边界"而拒绝。但水平 margin 只是**让位**，不是边界：Chrome 给该页的矩形是 `outer [21,53 50x0][96,53 50x0]`、`inner [21,53 50x50][96,53 50x50]`，即壳被展开、内容被切成两个 50x50，且宽度仍是 50（那 25px 的窄化必须留住）。

改法两条一起：
1. `IsOversetWrapper` 只拒绝**垂直** margin（水平 margin 允许，壳仍是"什么都不画"）；
2. `SpliceOverset` 展开壳时把它的水平 margin **携带**到顶替它的子上（首子拿 start 边、末子拿 end 边），`MulticolChildren` 在量这个子时从 `share` 里扣掉这份携带、摆放时再把它加回 `margin`。

实测：`_shapeB` 现在画 `11,11 50x50` 与 `86,11 50x50`（Chrome 同值）。门禁：屏幕全量 1929 → **1928** 条、**0 新增失败名**、**`wpt/css-break/overflowing-block-003.html` 转绿**（css-break 34 → 33）；单测 2180；打印 189/198 九条同名；`flex-item-content-overflow-*` 模块 0 失败（上一刀在填充循环里加"越界即切"时它们各红 5300，这条展开路径不碰它们）。
### 9.96 段落里装不下的浮动推到下一栏：`line-after-unbreakable-float-after-padding` 转绿

该对的形状是"多栏容器只有一个段落子，段落里一个 `float:left; width:100%; height:75px; break-inside:avoid` 再加一行文本"，容器 `columns:3; column-fill:auto; height:101px; width:6ch`，段落的 `padding-top:100px` 使第一栏只剩 1px。Chrome 的矩形（`build/_la_probe.html` DOM 探针）：

- `para rect0 x=8 y=8 15.59x101`——只有 padding，没有内容；
- `float rect0 x=23.59 y=8 15.59x75`——浮动整块站在第二栏顶上；
- `para rect2 x=39.20 y=8 15.59x50` 与 `SS rect0 x=39.20 y=25 15.61x15`——文本在第三栏。

段落路径（`FloatHostOf` / `PlanFloatParagraph` / `Fill`）改了三处：

1. `FloatHostBand` 多报两个数：band 的下沿 `reach`，以及 band 起始那一行是否被浮动占满宽度 `linesBelowBand`（占满就一行也并排不了，只能垂在浮动底下）；
2. `Fill` 里当 `top + reach > extent` 而一整栏装得下 `reach` 时令 `bandColumn = 1`：浮动从第二栏起站，它前面的栏只放段落自己带上边界的那一片（0 行）；band 那一栏若连第一行也放不下，就把文本整条让到下一栏并按整栏宽重排。续接片用 `new InlineStart(host.Host, 0)` 作记号，因为 `ArrangeBlockLines` 只在 `start is null` 的那一片画浮动——空白的起点正是"续接但还没开始"；
3. 非末栏的 `Fitting` 返回 0（`orphans >= 2` 时会）时，文本让给下一栏，不再套用"fill 的末栏吞掉其余全部"那条。

量出来的一个坑：`LineHeights(..., banded)` 返回的第一行高度**已经**含 band 的垂下（该页 125 = 75 + 50），所以 `room` 里不能再扣一次 `reach`；第一版扣了，`FragmentSlicer.Fitting` 的"片段必须推进一行"规则照样返回 1，文本仍留在浮动那一栏。

这条路径的覆盖面是量出来的：临时把 plan 的栏数/`reach`/`linesBelowBand` 落到 `build/_floathost.txt` 跑一遍全量屏幕语料，整张语料只有这一对走 `FloatHostOf`。

门禁：屏幕全量 finding 2003 → **2002**、**0 新增失败名**、**`wpt/css-break/line-after-unbreakable-float-after-padding.html` 转绿**（css-break 33 → **32**）；单测 2180；打印 189/198 九条同名。

余下的浮动对，`widows-orphans-021`（2500）已量过：Chrome 给 `green [8,8 50x100][58,8 50x100]`、`float [58,33 50x25]`——`orphans:3` 把三行整条逼进第二栏，浮动跟着站在第二栏 flow 位 25（即 L1 之后），而 Hutu 把绿盒当成整块切成两片各 125 高、第二片从 y=-25 起画，栏里只露出 50。另两对 `float-017` 663、`fragmented-autowidth-fc-root-beside-floats` 34400 仍待"高盒被栏切开 + 每片各自与旁边浮动求自由宽"。
### 9.97 orphans/widows 只数行盒：`widows-orphans-021` 转绿

`CutAt` 过去把「盒子里的内容单元数」当成 orphans/widows 的计数对象；CSS Break 3 §4.3 说的是 **line box** 的数量。二者只有在盒子里混了非行子元素时才分道扬镳。

Chrome 的 `widows-orphans-021` 矩形（`build/_wo21_probe.html`）：`green [8,8 50x100][58,8 50x100]`、`d25 [8,8 50x25]`、`float [58,33 50x25]`。容器 `columns:2; height:100px; line-height:25px; orphans:3; widows:1`，绿盒内容 = 一个 25 高空盒 + `br` + 一个整宽 25 高浮动 + `&nbsp;`+`br` + `br`，共 **3 行**。第一栏的剩余空间放得下浮动，但 orphans:3 不许把 3 行拆开，于是三行整块挪到第二栏，浮动跟着站在第二栏的 flow 位（离栏顶 25）；第一栏只剩那个小盒，Chrome 仍把这一片拉到满栏高（背景占满）。Hutu 用单元数判定「第一片留了 3 个单元 ≥ 3」就把切点落在 75，第二片只剩 50 高，第二栏下半截红着（2500 px）。

改法：`PieceBreaks` 的返回值不再是裸的 `Double` 列表，而是 `PieceUnit(Offset, IsLine)`——来自 `LineBreaks`（子元素按行断）的点是行，叶子子元素末尾的点是「整子元素」；`CutAt` 只对 `IsLine` 的点累加 head/tail 行数，并允许**一片里一行都没有**（整条 run 从下一片开始）。混排栏那条 `CutDownColumns` 只需要位置，故用新加的 `PieceOffsets` 取裸偏移。

对纯文本盒子（单元全是行）这条规则与旧的一致：`widows-orphans-022/023`（`orphans:1; widows:3`）切点仍是 75，19 条 `widows-orphans` 全绿。

门禁：屏幕全量 finding 1927 → **1926**、**0 新增失败名**、**`wpt/css-break/widows-orphans-021.html` 转绿**（css-break 32 → **31**）；单测 2180；打印 189/198 九条同名；`floats-clear` 16、`flexbox-multi-row` 5、`multicol` 107、`flex-item-content-overflow` 0 与 HEAD 一致。

余下：`fragmented-autowidth-fc-root-beside-floats` 34400 与 `float-017` 663 等「高流内盒被栏切开 + 每片与旁边浮动求自由宽」；abspos 一族（`abspos-in-opacity-003` 6000、`out-of-flow-in-multicolumn-045/073/090` 10000/11600/11600、`block-end-aligned-abspos-with-overflow` 3000）需要**绝对定位盒自己进栏的切片队列**：045 的探针显示 Hutu 把 `.rel` 切成两片、每片都重画整条 abs（`fill 8,49.72 50x200` 与 `fill 58,-50.28 50x200`），绿盖住了红，但两片的 abs 各自被本栏 clip 裁掉一段，仍差 10000 px（正好一整栏的面积）。
### 9.98 三条线索的现场测量（OOF 045/003、flex 044、竖排 min-block-size 2）：本轮不动代码，只把缺口写死

本会话落地两刀（`7395b92e` 段落浮动推栏、`51216991` orphans/widows 只数行盒）后，用 Chrome DOM 探针把余下几族的**确切缺口**量清，避免下次从像素反推：

1. **`out-of-flow-in-multicolumn-045`（10000，整盒全差）**。Chrome：`mc [-102,-50 100x100]`；被切的 wrapper 两片 `[-92,-50 40x100]`、`[-42,-50 40x100]`；里面的 `.rel`（`top:100;left:100`）两片 `[8,50 40x100]`、`[58,50 40x100]`；孙子 abs 两片 `[8,50 50x100]`、`[58,50 50x100]`。即**相对位移把整个片段连同它的裁剪带一起搬走**，而 abs 只按 CB 片段在**块向**被切（栏向不裁，所以 abs 宽 50 > 片宽 40）。Hutu 今天裁剪带仍停在未位移的栏位置 `[-92/-42,-50.28 40x100]`，内容画在位移后的 `(8/58,49.72)` → 两片全被裁光，整块什么都不画。试落“`FragmentClip` 随 `RelativeDisplacement` 平移”（`build/_clip-move.patch`，73 行，改三处块站点），**实测对 045 无效**——位移在内层 `.rel` 上，不在被切的 wrapper 上，故未落地、存为补丁。缺的那半是 **OOF 后代自身的分栏**（每个 CB 片段产出一个 abs 片段，只在块向裁剪）。
2. **`abspos-in-opacity-003`（6000）**。Chrome：`rel [48,108 20x0]`（CB 自己掉到容器下方），而 `top:-300` 的 abs 出 **5 个片段** `[8/28/48/68/88, 8 20x100]`——**包含 CB 之前的三栏**。Hutu 只从 CB 所在栏往后切片，于是前三栏是纯绿 `#008000`，ref 是叠了 10% 红的 `#1a7200`，差 3×20×100=6000。要的是“OOF 盒自身的矩形向上越界时，也要给它之前的 fragmentainer 产出片段”（backward pieces），与 §9.34 的 reach（只算块向下）成对。
3. **`flexbox/single-line-row-flex-fragmentation-044`（1000）**。ref 里橙色项留在第一栏 `x=50..100`，粉色项进第二栏 `x=100..150`；Hutu 两项都留在第一栏。`RelaysInColumns` 见到 `IsRow` 就拒、`RowRelaysInColumns` 要求 `Wraps`，且两者都先撞 `columnHeight <= 0` 直接拒绝——本对的容器 `height:0`。规则口径应是：**块起点（含 margin）已越过 fragmentainer 末端的项，从下一栏起站；起点还在房间内的项留下并溢出**。
4. **`break-inside-avoid-min-block-size-2`（133840）**。竖排 `columns:3` + `break-inside:avoid` + `min-block-size:40px`。Hutu 树里同栏两个子盒被**沿行内轴堆叠**（`720,0 40x160` 与 `720,170 40x160`），而不是按块轴向栏内分配；即 §9.40 那句“竖排的块向边映射、`HoldsPositioned` 与表格豁免都只在横排生效”在这里还成立，avoid 的竖排栏分配整体缺席。

同一轮复看：`float-017`（663）与 `fragmented-autowidth-fc-root-beside-floats`（34400）仍在等“比一栏还高的流内盒被切开、每一片各自与旁边浮动求自由宽”——`FloatFlowColumns` 那条“有比栏还高的流内盒就退回常规路径”的排除仍有效。

代码未动，HEAD 仍是 `51216991`：屏幕 1926 条 finding、css-break **31** 对、单测 2180、打印 189/198 九条同名。
### 9.99 混排栏改成“一栏一态”：被推走的浮动不再把流带走，比栏还高的流内盒按栏切开、每片读自己栏里的自由宽

`fragmented-autowidth-fc-root-beside-floats`（34400）的 ref 是三个 200 宽假栏里的三块热粉：宽 **100 / 10 / 200**，各 100 高。页面是 `columns:3; column-gap:10px; column-fill:auto; height:100px` 里两个右浮动（100x60，以及 `clear:both; break-inside:avoid` 的 190x60）后面跟一个 `display:flow-root; height:300px`。也就是说：300 高的 BFC 根要被三栏切成三片，每片的宽取决于**它所在那一栏**还剩多少自由空间（第一栏旁边是留下的 100 宽浮动 → 100；第二栏旁边是被推过来的 190 宽浮动 → 10；第三栏没有浮动 → 200）。

旧模型两处不到位，都是量出来的：

1. 混排栏只有一条 `cell/tracks/flow` 游标，`NextColumn()` 把三者一起清零 —— 于是**被推到下一栏的浮动把“流”也带走了**，后面的流内盒从第二栏起站，第一栏白白空着；
2. 浮动的“放不下”判据用的是 `flow + margin.Top`，而 `clear:both` 的下沉是 `tracks.Place` 内部偷偷做的 —— 判据看不到下沉后的真实 y，本该推栏的浮动留在原栏叠到了 120 高。
   还有一条判据直接把这个形状挡在门外：`FloatFlowColumns` 见到“流内盒比栏还高”就整容器退回常规路径（上一轮量过：只放开判据而不做每片取宽，会从 34400 恶化到 89000）。

改法：把混排栏换成**每栏一份状态**（`FloatFlowColumn`：该栏的 `tracks`、该栏装的 `items`、该栏走到的 `flow`），`at` 只表示“流所在的那一栏”。

- 浮动放不下时只把自己搬到后面的栏（栏按需创建，流留在原栏不动），后面流走到该栏时自然遇见这颗浮动。搬过去的那一栏未必装得下它：`gecko/bugs/563584-9d`（三个 200 宽、栏宽也是 200 的浮动）量出这一点 —— 只搬一栏会把第三颗浮动叠到第二栏的浮动底下（60000 px 全红），所以要在 `at+1、at+2…` 里找**第一栏装得下整颗**的：`tracks.FitBelow(0, width)` 先按现有浮动下沉到能容下它宽度的带，再看栏高够不够；装不下一栏的超高浮动（`outer > columnHeight`）仍按老路子搬到下一栏后 `CutDownColumns` 续切；
- 带 `clear` 的浮动起点改成 `tracks.ClearBelow(clear, flow) + margin.Top`（CSS 2.1 §9.5.1：没有 clear 才谈得上“接着流站”），推栏判据因此看得见真实落点；
- 比一栏还高、自身不是行盒容器的流内盒走 `CutDownColumns` 一片一栏，每一片的宽取所在栏的 `tracks.FreeSpanAt(y, visible)`（BFC 根不得压过浮动，CSS 2.1 §9.4.1），左右再各让出自己的水平 margin。有行盒的盒子仍交回常规栏，因为每一片都得重新断行。

实测：`wpt/css-break/fragmented-autowidth-fc-root-beside-floats.html` **转绿**（34400 → 0），三片宽 100/10/200 与 ref 逐值相同；`float-017` 仍 663（它的残差不在这条路上）。`overflowing-block`（2 对）与 `flex-item-content-overflow`（4 对）模块 0 失败，`floats-clear` 16、`gecko/flexbox/flexbox-multi-row` 5 与 HEAD 一致。

门禁：单测 2180；打印 189/198 九条同名；屏幕全量 finding 1926 → **1925**、**0 新增失败名**（第一次未加“找装得下的栏”那条时被 `gecko/bugs/563584-9d` 挡下，补上后回归消失）。

### 9.100 声明 size 局限的盒子是 monolithic：栏不从它中间切；`float-017` 663 → 653

用 DOM 探针对着 Chrome 量了一组只差 `contain` 的变体（`build/_f17_matrix.html`、`build/_f17_matrix2.html`；容器一律 `width:20em; columns:4; gap:0; column-fill:auto; height:100px`，即栏宽 80、栏高 100）：

| 浮动的写法 | Chrome 给这个浮动的矩形 |
| --- | --- |
| `contain:size; width:100%; height:200`（float-017 原样，前面有一行"1"） | 一片 `x=81 y=1 80x200`——整块站在第二栏顶上，超出栏底 |
| 同一个浮动去掉 `contain:size` | 三片 `80x80`(第一栏剩下的) + `80x100` + `80x20`——按栏边界切开 |
| `contain:paint` | 与去掉 contain 相同：三片 |
| `contain:layout` | 三片 |
| `contain:size; width:10px; height:200`，浮动是首子 | 一片 `10x200` 站在第一栏，既不切也不推 |
| `contain:size; height:150`，浮动是首子 | 一片 `10x150` |
| 前面 30 高的块 + `contain:size; height:90`（第一栏只剩 70） | 一片 `10x90` 站在第二栏顶（整块推栏） |

结论只有一条：**size 局限让盒子 monolithic**（CSS Contain 1 §3.1.1 原文 "Size containment boxes are monolithic"，并指向 CSS Break 3 §4.1 的可能断点），栏不从它中间切；它前面的地方放不下就整块推到下一栏，在新栏里照样溢出去。paint / layout 局限没有这个效果（上面两行实测）。

改三处判据，同属"盒里没有可断的内容，就按栏边界切空盒"这一族：

1. `AddBoxCut` 的拒绝表加 `IsMonolithic(child, vertical)`；
2. `CutsAsBox`（弹性项那一处同样的判据）加 `!IsMonolithic(child, vertical)`；
3. 混排栏 `PlaceFloatFlowColumns` 里"比一栏还高的浮动续切"之前，monolithic 浮动走整块那支（本路径只在横排进入，写 `IsMonolithic(child, false)`）。

新加的 `IsMonolithic(box, vertical)` 按栏切的轴读局限：横排的栏切块向 → `ContainsBlockSize`，竖排的栏切行内向 → `ContainsInlineSize`（`contain:size` 两位都置，`contain:inline-size` 只置行内向）。`DividesUnits` 没动——那是"盒里有普通子块"的一族，Chrome 在**有内容**的 monolithic 盒上的切法本轮没量过，不凭空加规则。

实测：`wpt/css-break/float-017.html` 663 → **653**。`f1` 现在画 `88,49.72 80x200`，与 Chrome 的 `88,50 80x200` 同值（原先是一片 80x200 再两片续切，把流带走了）。剩下 653 全在 `fr`：Chrome 给它四片 `8,70 80x80`（第一栏，行 2-5）、`88,201 80x0`（第二栏被浮动带挤到栏底之外，零高一片）、`168,50 80x100`（行 6-10）、`248,50 80x100`（空）；Hutu 的 `fr` 第一片落在第三栏顶 `168,49.72`——常规栏把块级浮动当成**占流的一员**，浮动推栏时把流一起带走。这正是混排栏 `e4bf3430` 已解决、而本容器进不了混排栏的那条排除：`FloatFlowColumns` 不收"自身比栏还高、且按行断的流内子"（§9.93 判据①；放开量过，`fragmented-autowidth-fc-root-beside-floats` 从 34400 恶化到 89000），因为每一片都得重新断行。要收掉这 653，要么让常规栏的浮动也不占流，要么给混排栏一个按片重排文本的能力（任务 #5），本轮记下不动。

三道门禁：单测 2180；打印 189/198、九条同名；屏幕全量两侧同名 finding 2000 条（gecko 603 / wpt 1397 / wpt-variants 0），**0 新增失败名、0 转绿**。

### 9.101 给 monolithic 那条补三条单测；顺手量到一个 orphans 的新差异（本轮不动代码）

`src/Hutu.Tests/MulticolBoxCutTests.cs` 加三条，都是从 CSS/Chrome 能直接推出来的数：

1. `contain:size` 的空盒 250 高站在 100 高的栏里——**只有一片** `0,0 100x250`（CSS Contain 1 §3.1.1：monolithic 没有可断点）；
2. `contain:size` 的浮动（50x200）在前一个 80 高的盒只剩 20 的时候**整块推到第二栏**，站成一片 `150,0 50x200`（不续切）；
3. 比栏还高的 `display:flow-root`（300 高）被三栏切成三片，宽分别 **20 / 40 / 100**：第一栏旁边是留下的 80 宽浮动，第二栏旁边是被推过来的 60 宽浮动（`clear:both; break-inside:avoid`），第三栏没有浮动——即 `e4bf3430` 的"一栏一态"，浮动推走不把流带走。

写第三条时先试了"盒里有块子元素"的 orphans 形状，量出来是**新的差异**，记在这里（`build/_orph_probe.html`；容器 `width:160px; columns:2; gap:0; column-fill:auto; height:50px; orphans:3; widows:1`，内容 = 20 高的块 + `#g`：`AA`、`<div style=height:10>`、`BB<br>CC`，即 3 行 + 1 个块子）：

- Chrome：`g r0 0,20 80x30` + `g r1 80,0 80x20`——第一栏留下 **1 行 + 那个 10 高的块子**就切了，`orphans:3` 没把整条 run 逼走；
- 与 §9.97 的 `widows-orphans-021` 相对：那里第二栏装得下整条（100 高），Chrome 确实把 3 行整块逼进第二栏；本形第二栏同样装得下（40 ≤ 50），Chrome 却照切。
- 两处唯一区别是**断点的性质**：021 的断点落在行与行之间，本形的断点落在一个块子元素的末尾——Blink 不按 orphans 计数这种断（`BreakLayout` 只在行间断里做 orphans 挑选，块间断另有其规则）。
- Hutu 本形给两片：第一片是**整盒 40 高**站在 `0,20`（越过栏底 10），第二片在第二栏——与 Chrome 的 30+20 不一致。本轮不补这条：没有任何 css-break 对靠它，且判据（哪种断点才受 orphans 管）还得再量一组变体才能立住，写单测就会是红的。

门禁：单测由 2180 增到 2183（三条全绿）；本轮 `src/Hutu` 未动，屏幕/打印两个口径沿用 `14af60d2` 的量：全量 1925 条 pixel-diff finding、css-break 30 对、打印 189/198 九条同名。

### 9.102 单列填充的嵌套壳被外层展开：`variable-fragmentainer-size-002` 转绿，-001 2400 → 2000

`ColumnsOf` 有一条老排除：子盒自己也是多栏就整个容器退回普通堆叠（"它要在本层的一栏里平衡自己的几栏"）。这条把 001/002 整对挡在分栏之外——探针对 `variable-fragmentainer-size-001` 量过：外层的 300x100 里那个 `columns:1; column-fill:auto; height:250px` 壳站成 `8,49.72 300x250`，**连栏宽都没拿到**，里面 170 高的表整块画在第一栏。

新加的判据是 `IsSingleColumnFragmentainer`：单子列、`column-fill:auto`、不 `column-wrap`、无 border/padding/margin/背景/裁剪、无强制断与 `break-inside:avoid`、子里只有流内块。这种壳**没有要在栏里平衡的几栏**——它就是一间给内容的房，房本身什么都不画，切的是内容（CSS Multicol 3 §7.4、CSS Break 3 §4.1）。两处一起改：`ColumnsOf` 不再为它拒绝分栏（栏宽正常发给它），`SpliceOverset` 的"裸壳展开"接受它（`IsBareWrapper || IsSingleColumnFragmentainer`，配合"壳比栏还高"那条原有条件）。

实测：
- `wpt/css-break/table/repeated-section/variable-fragmentainer-size-002.html` **转绿**（800 → 0）；
- `-001` 2400 → **2000**（表进了三栏，重复表头已经在第二栏顶上画出来了；残差见下）。

**下一片的判据已经量死**（`build/_repeat_matrix.html`、`build/_repeat_matrix3.html`；表 = 20 高 header(avoid) + 130 高 row + 20 高 footer(avoid)，外层 `columns:4; column-fill:auto; height:100px`，内层 `columns:1; column-fill:auto; height:250px`，前置一个 N 高的块）：

| 前置块 N | 表在第一栏拿到的房间 | Chrome 画的 header / footer 矩形数 |
| --- | --- | --- |
| 0 | 100 | 3 / 3 |
| 10 | 90 | 3 / 3 |
| 20 | 80 | 3 / 3 |
| 30 | 70 | **1 / 1** |
| 40 | 60 | 1 / 1 |
| 50 | 50 | 1 / 1 |
| 60 | 40 | 1 / 1 |

即两条：①**重复段要占栏空间**——001 的三栏内容分配是 80 / 60 / 30（第一栏 20 header + 60 row + 20 重复 footer = 100），而 Hutu 的 `AddBoxCut` 按整栏高切内容（100 / 70），重复段是叠画不占位；②**第一栏的房间不足 2×(header+footer) 就整表不重复**——阈值实测落在 80（≥ 40×2 画，70 不画），且一旦第一栏不给，后面 100 高的栏也不补画（`lead30` 的 header 只有 1 个矩形）。要做这半，得把重复段高度从 `TableFormattingContext.Build`/`SectionBand` 暴露给 `AddBoxCut`，并让 piece 的**画高**（含重复段）与 slice offset 的**推进**（只走内容）分开；002 现在能过，正是因为它的分配恰好等于"不重复"那一支，改的时候必须把这条阈值一起写进去，否则 002 会翻红。

三道门禁：屏幕全量 finding 1925 → **1924**、**0 新增失败名**、0 对变差、**`variable-fragmentainer-size-002` 转绿**（css-break 30 → **29**）；单测 2183；打印 189/198 九条同名。

### 9.103 竖排那两对卡在"竖排容器从不切块向超栏的盒"——量死，留给轴向那一片（不动代码）

`gecko/css-break/vertical-wm-001/002`（27468 / 24688）看着像"块向边映射"没做，探针量下来更前面一步就没走通：页面是 `columns:2; column-gap:0; column-fill:auto; block-size:90px; inline-size:140px` 的 `writing-mode: vertical-lr` 容器，里面一个 `div.inner`（`border-block-start:25 blue` + `border-block-end:17 grey` + `inline-size:70`，装两个 `break-inside:avoid` 的 20x20）。竖排里块轴是横向，所以**每栏宽 45**，而这个盒沿块轴占 82。

Hutu 的树（`build/_probe-vwm001.txt`）：容器 `60,60 92x142 vertical` 正常（block-size 90 + 边框 = 92 宽 ✓），但它的子**整块站成一个** `101,61 82x70 vertical m:0/0/0/40`——x 从 101 到 183，越过容器右缘 152；绘制里 `border 101,61 82x70 w:0/17/0/25` 一条命令同时画了蓝（101..126）和灰（166..183）。ref 是把这些子拆成独立块，让 Chrome 在栏边上的**竖向切线**自然落位：第一片拿蓝边，第二片拿灰边。残差签名（test 在 `x=101..125` 画蓝而 ref 白）正是"两片都认领了块开始边"。

方向没错、判据也不缺——缺的是**入口**：竖排容器的分栏计划里没有一条会去切"块向长度超过栏宽"的子。所有 divide/relay 判据都显式拒绝竖排：`DividesAcross`（`fills && !vertical && …`）、`RelaysInColumns`、`RowRelaysInColumns`、`FloatFlowColumns`（`box.Flow.IsVertical` 即退）、`BreaksAtLines`（`!child.Flow.IsVertical`）；`AddBoxCut` 反而带 `vertical` 形参、`BlockExtentOf(size, vertical)` 也按轴取值——也就是竖排的"切空盒/切片"半边已经有几何（`ArrangeMulticolRunAcross` 里 `child.Offset >= 0` 那一支已经按 `blockReversed` 映射了切片边距），但没有谁会为一竖排的子调用它。这与 §9.40/§9.81 记的是同一条轴向缺口，也正是 8 对竖排表（`table-grid-paint-vrl/vlr-*`、`table-section/row/col-paint-*`、`table-collapsed-borders-paint-vrl/vlr-*`）等的同一半——任务 #8。同一条还压着 `break-inside-avoid-min-block-size-2`（133840，§9.98 第 4 条：竖排 avoid 的两个子在栏内**沿行内向轴**堆叠）。

所以本轮不动代码：把竖排的轴向做进分栏判据，是一片的量（判据 5 处 + 竖排表的行/列带），且必须与 8 对竖排表一起量，不能只对着这两对调。

当前基线（`93d2527b`）：屏幕全量 1924 条 pixel-diff finding、css-break **29** 对；单测 **2183**；打印 **189/198**、九条同名（无 css-break）。

### 9.104 `variable-fragmentainer-size-001` 剩下的 2000 需要"内容窗"和"画高"分成两个数——管道摸清了，不动代码

把这条要动的地方逐处量清了（省得下次再考古）：

1. 表的片是 `AddBoxCut` 切出来的（`child.Offset >= 0` 的那些片），竖排走 `ArrangeMulticolRunAcross` 的 `child.Offset >= 0` 支、横排走 `MulticolChildren` 之后的 `child.Offset >= 0` 支（`BlockLayout.cs:5798` 起）；这一支给 `_table.Arrange` 的 `FragmentSlice` **已经带** `Offset = child.Offset`、`Height = height`。
2. `RepeatSections` 的定位是 `top = borderBox.Y + fragment.Offset`、`bottom = top + fragment.Height`。因为摆放用的是"整盒上移 Offset"（`whole = rect.X, y - child.Offset, width, child.Whole`）+ `FragmentClip = rect`（画高 `height`），所以 `top` 恰好等于栏顶 ✓，而 **同一个 `Height` 既当裁剪窗、又当表脚重复带的下沿**。
3. Chrome 要的是两个数：内容窗 = 房间 − 重复表头 − 重复表脚（001 是 80 / 60 / 30），画高 = 房间（100 / 100 / 50）。表头重复画在栏顶（现在就是这样），表脚重复要画在**画高**的底；裁剪却只该露出**内容窗**。今天这两个数是同一个 `height`，所以扣了空间就会把重复段裁进内容里（表脚压在最后 20 的内容上），不扣就什么都不占（现状）。

要落这条，最小改动是：`FragmentSlice` 再加一个"内容窗外还要展示多少"（例如 `Shown`/`Room`），`AddBoxCut` 对**有重复段的表**把片的内容extent按房间扣掉 `head`（非首片）与 `foot`（非末片），`MulticolChild` 的 `Size.Height` 继续带画高、`offset` 只按内容推进；`RepeatSections` 的 `bottom` 用画高、裁剪用内容窗。重复段高度要从 `TableFormattingContext.Build`/`SectionBand` 暴露（现在只有 arrange 内部用）。

阈值（§9.102 的矩阵）必须同批进来：表在第一栏拿到的房间 ≥ 2×(head+foot) 才启用重复并扣空间，否则整表不重复——`variable-fragmentainer-size-002` 现在过的就是"不重复"那一支，只加扣空间不加阈值会把它翻红。竖排那两对（§9.103）与本条无关，别一起改。

### 9.105 落了一遍"重复段占栏空间"：切分算式复现出 Chrome 的 80 / 60 / 30，但一条绿对翻红、-001 只剩画的问题，整片回滚

补丁存 `build/_repeat-room.patch`（277 行，三处文件）。做法就是 §9.104 那条：

1. `MulticolChild` 加 `Lead`/`Trail`（本栏为重复表头/表脚让出的房间）；`AddBoxCut` 里 `head`（非首片）与 `foot`（表还往下走）从房间里扣，`offset` 只按**内容**推进，而给栏切片器看的 `Fragment.Height` 带上 lead + trail；
2. 阈值照 §9.102 的矩阵写死：`columnHeight - 已流入高度 % columnHeight >= 2 * (head + foot)` 才启用（这条把 `-002` 挡回"不重复"那一支，实测它仍绿 ✓）；
3. 摆放处 `standing = y + Lead`：整盒放在 `standing - Offset`、裁剪窗 `(x, standing, width, content)`，`FragmentSlice` 传 `Offset = child.Offset - Lead`、`Height = Lead + content + Trail` —— 这样 `RepeatSections` 一行不改也能把表头重复放在栏顶、表脚重复放在栏底；
4. 段高从 `TableFormattingContext.RepeatedBands`（`Build + MeasureCaptions + MeasureOthers + CompleteGrid` 之后读 `SectionBand`）经 `LayoutEngine.TableRepeats` 暴露给栏。

逐对量下来（`HUTU_SCORE_LOOK=css-break/table`，全族 30 对）：

- `-001` 2000 → **1600**，片分配已是 Chrome 的 80 / 60 / 30（第一栏 header 20 + 60 内容 + 重复 footer 20 = 100，第二栏重复 header 20 + 60 + 20，第三栏 20 + 30 = 50）；残差从"分错了"变成"画歪了"。
- 把 `RepeatSections` 的早退从 `Offset > 0.001` 改成按 `!fragment.Starts` 判表头（第一片也该画表脚重复）之后，**1600 一字未动** ⇒ 这半不在"画没画"，在裁剪带/相位的算法上（`FragmentClip`、`BackgroundOrigin` 与新加的 `standing` 之间）。
- `text-in-nested-multicol` 由绿 → **156**（首点 `25,54` 是头部那对嵌套两栏里第二个字母的 AA 灰）。这一对只有 header（20）没有 footer。第一片的算式与旧行为完全相同（foot = 0 ⇒ 内容仍占满 100），差别只可能是**计划阶段多跑了一次 `Build/CompleteGrid`**——怀疑它污染了表的测量缓存，而不是 lead 几何本身。这条要单独验：把 `head/foot` 改成从测量阶段已经算好的 `TableBuild` 里取（`Measure` 里 `CompleteGrid` 之后顺手记一段高），别在栏的计划里重新 `Build`。
- 其余表对一字未动：`table-border-008`、`table-collapsed-borders-paint-htb-ltr`、`table-caption-and-cells(-fixed-width)`、`specified-block-size-007`、`abspos-uncontained-text`、八条 `table-fragmentation-*-print` 全都仍 "the two agree" ⇒ `!fragment.Starts` 那处判据改动无附带伤害（可以单独留用）。

门禁按规矩不过（新增 1 个失败名），整片回滚；`git diff` 存 `build/_repeat-room.patch`（277 行），回滚后重编译并复核：`repeated-section` 回到 0 / 700 / 2000 / 0，`css-break/table` 全族与 `93d2527b` 一字相同，单测 2185 绿。

下一片的两条入手点（已经排除掉的假设也记着）：(a) 段高从测量阶段带出来，别二次 `Build`；(b) 1600 是四个 400 px 方块的**位置**问题，拿 `build/look/variable-fragmentainer-size-001-{test,ref}.png` 与残差带对着量，别再猜"没画重复"。

### 9.106 重复表头/表脚占栏空间落地：`variable-fragmentainer-size-001` 转绿（css-break 29 → 28）

按 §9.104/§9.105 记的做法落地，四点：

1. `MulticolChild` 加 `Lead`/`Trail`——本栏为重复段让出的房间。`AddBoxCut` 里 `head`（非首片）与 `tail`（表还往下走）从房间里扣：`room = columnHeight - clonedInsets - headMargin - head`，还要往下走时再 `content = min(content, room - repeatFoot)`、`tail = repeatFoot`；`Fragment.Height` = lead + content + trail（栏切片器看的），而 `offset` 只按**内容**推进。
2. 阈值同 §9.102 量死的：`columnHeight - 已流入高度 % columnHeight >= 2*(head+foot)` 才启用；`-002`（第一栏只剩 50）因此仍走"整表不重复"那一支，一字未动 ✓。另加一条"扣完空间后片数不能超栏数"的自等式，超了就不扣（保持旧行为）。
3. 摆放与裁剪分开，这是转绿的关键：整盒仍按 `standing = y + Lead` 平移，但**片子的 `FragmentClip` 必须是画高（lead + content + trail）而不是内容窗**。上一版把裁剪留成内容窗，`-001` 卡 1600、`text-in-nested-multicol` 还翻红 156——因为重复段的 copy 是这个片的**子节点**，父 clip 从 `y+Lead` 起就把它们整条裁掉了。`FragmentSlice` 相应传 `Offset = child.Offset - Lead`、`Height = Lead + content + Trail`，`RepeatSections` 的 `top = borderBox.Y + Offset`、`bottom = top + Height` 于是仍正好落在栏顶与栏底。
4. `RepeatSections` 早退条件从 `Offset > 0.001` 改成只要求 `Height > 0.001`，表头 copy 改按 `!fragment.Starts` 判——**第一片也该画表脚 copy**；段高不再在栏的计划里重新 `Build`，而是 `Measure` 走完 `CompleteGrid` 后存进 `ConditionalWeakTable<Box, Bands>`，栏再问取（`LayoutEngine.TableRepeats`）。实测第二次 `Build` 不是 `text-in-nested-multicol` 翻红的原因（改完仍 156），真正原因还是第 3 点的裁剪。

实测：`wpt/css-break/table/repeated-section/variable-fragmentainer-size-001.html` **转绿**（2000 → 0）。三道门禁：单测 **2185**；打印 **189/198**、九条同名；屏幕全量连跑两遍 finding 1924 → **1923**、两遍逐对像素一字不差、与 HEAD 基线相比**只有这一对消失**、**0 新增失败名**（上一版看到的 css-lists/word-break/first-line ±2..144 在复跑里归零，确认那是跑动噪声）。`css-break` 剩 **28** 对。

### 9.107 OOF 那五对：两条猜测已量死并排除（clip 随位移、clip 沿行内向放宽），缺的是"abs 盒自己进栏队列"

在 `fb0636de` 之后试了 OOF 簇的两条便宜改法，都量过、都不成立，记死免得再试：

1. **`FragmentClip` 跟着 `RelativeDisplacement` 走**（`build/_clip-move.patch`，73 行，四处块站点）。对 045/073/090 **数字一字未动**。原因：被切、被 clip 的是**没有位移的外层 wrapper**，带位移的是里面的 `.rel`——补丁平移的是"片自己的位移"，外层片位移为零，所以是 no-op。
2. **把片的裁剪从"本栏宽"放宽到"整行栏宽"**（fragmentainer 只在该切的方向上切，行内向溢出照画——听起来很合 CSS Break 3 §4.1）。实测：045 仍 10000、073/090 仍 11600，一点没变；反而把原本绿的 `out-of-flow-in-multicolumn-001` 打红 **640 px**（`x=50, y=50` 该白处画了灰）⇒ **栏在内向确实要裁**，Chrome 那个"abs 宽 50 > 栏宽 40 还不被裁"不是靠放宽父片裁剪做到的。两次试验都已回退，树回到 `fb0636de`。

真正的缺口还是 §9.98 量的那条：**abs 盒自己要进栏的切片队列**。`abspos-in-opacity-003` 最干净：容器 `columns:5; width:100px; height:100px; column-fill:auto`（五栏各 20 宽），子是 300 高空块 + `position:relative; opacity:.1`，其内 `top:-300px; width:100%` 的 abs 装 500 高红块。Chrome：abs 出 **5 片** `x=8/28/48/68/88, y=8, 20x100`——**包含 CB 之前的三栏**（CB 自己在第 4 栏 `[48,108 20x0]`）；Hutu 只从 CB 所在栏往后切，于是少三栏 = 3×20×100 = **6000 px**，与残差一字不差。也就是：abs 的矩形跨到哪些 fragmentainer，就在哪些 fragmentainer 里各画一片、按该 fragmentainer 的块向窗口裁；它前面的栏也要有片。这一条同时是 045/073/090 缺的"abs 自己分栏"那一半，已记成任务 #11。

当前：`dev-paged` HEAD `fb0636de`（工作树对 HEAD 干净），屏幕 1923 finding、css-break **28** 对、打印 189/198 九条同名、单测 2185。

### 9.108 OOF 那五对不是一个机制：量完拆成三条，各自记死（本轮不动代码）

探针 + 差分带把 045/073/090/003/block-end-aligned 分开了，三条互不相干：

1. **abs 要拿到"自己矩形跨到的每一栏"的片段，含 CB 之前的栏**（任务 #11）。`abspos-in-opacity-003`：容器 `columns:5; width:100px; height:100px; column-fill:auto`，子里 300 高空块 + `position:relative; opacity:.1`，后者装 `position:absolute; top:-300px; width:100%` 的 500 高红块。Chrome 给 abs **五片** `x=8/28/48/68/88, y=8, 20x100`。Hutu（`build/_probe-oof003.txt`）：300 块正确占 1-3 栏；`.rel` 因 `PositionedReach = top+height = -300+500 = 200` 只拿到 **4、5 两栏**的片段，每片把自己那份 abs 画在**本栏 x**（这点已经对），但 CB 之前那三栏根本没有它的片段 ⇒ 少 3×20×100 = **6000**，与残差一字不差。045 是同一件事 + 相对位移。要的是一条**覆盖式成员**：不占流、按指定栏落位——现在线性 plan（`MulticolChildren` → `RunCells` 按累计 extent 排栏）表达不了"同一个盒在若干栏各有一片且不推流"。
2. **底锚定 abs 的内容要按 fragmentainer 续切**（任务 #12）。`block-end-aligned-abspos-with-overflow`：差分带只有两条（`build/_look-bea.txt`）——第一栏 `x=8..57, y=138..157` test 绿 / ref 粉；第二栏 `x=58..107, y=68..107` test 白 / ref 绿。Hutu（`build/_probe-bea.txt`）把 abs 盒量成 `20x10`（只取首子），60 高内容整体往下溢；第二栏那片 CB 又把整个 abs 重画一遍。Chrome 是把 abs 的**内容**切开：第一栏留粉尾、第二栏顶上放整块 50x50 绿。
3. **嵌套 fixed 受定位祖先影响**（任务 #13）。073/090 标题都是 "Nested fixedpos should be affected by relative positioned offset"，容器带负 margin；Hutu 的红（在 `PushTransformCommand` 里）与绿 fixed 画在**同一个矩形** `-42,149.72 50x200`，整块在页面可视区外 ⇒ ref 的绿看不见（11600）。这两对是 Chromium-bug 参照，按 ref 的理想落位做，别照抄 Chrome 的怪癖。

结论：003 那半是 plan 的表达力问题（覆盖式 OOF 成员），不是一行判据能改对的；已按三条分开立任务，各自带本节量的矩形/差分带，免得下次重新考古。仓库状态未动：`dev-paged` HEAD `a7b2623a`，屏幕 1923 finding、css-break **28** 对、打印 189/198、单测 2185。

### 9.109 abs 盒"往前栏"落地：空的定位祖先按片段铺进它越过的栏，`abspos-in-opacity-003` 转绿（css-break 28 → 27）

做法（§9.108 那条设计的落地版）：`ArrangeMulticolChildren` 排完正常栏后调 `ArrangeBackwardFragments`——对每个计划成员按累计 `Fragment.Height` 算出它在线性坐标里的起点 `start`，再取出它**直接**的 `position:absolute` 子里写了负 `top` 的那些矩形 `[start+top, start+height)`，只取其中**高于 `start`** 的那一段；这一段跨到的每一栏（下标夹到 ≥0，且 < 容器实际栏数）各放一片：片是**祖先盒**摆在自己那条栏里的原位（`standing = content.Y + start − 栏窗起点`，因此 abs 子自然落在正确高度），裁剪带取 `[max(from,窗顶), min(to,窗底)]`——只露出该 abs 子覆盖的那一条，栏里别的内容归它自己的片段。祖先的 `opacity`/`filter` 层随这片一起过去（003 要的正是 `opacity:.1` 的红叠在绿上）。

三条判据都是量出来的，缺一条就翻车：
1. **栏下标夹到 ≥ 0**：容器的第一栏之前没有 fragmentainer，越出去的内容就是普通溢出。不夹时 fieldset 那条会往 `col=-1`（容器左边的页面上）画三片。
2. **裁剪带用 abs 子自己的矩形**，不是整栏窗口：`fieldset-001-ref` 用 `legend{position:absolute; top:-18px}` 模拟表头，整窗裁剪会把祖先的正文一起搬进前一栏（实测 +2640 px）。
3. **只对"自己没有高度"的祖先生效**（`child.Size.Height <= 0.001`）：003 的 `.rel` 是个空的相对定位盒，它的全部画高就是那个 abs 孩子；有正文的盒（段落、fieldset）越出去的部分属于它自己的溢出，Chrome 不会替它在前一栏重画。这条一加，`fieldset-001` 回到基线一字不差。

legend 单独立了排除（HTML rendering §15.3.12 由 fieldset 自己贴边框放），但真正的兜底是第 3 条——`IsLegend` 那条没挡住 ref 里的绝对定位表头。

门禁：单测 2185；打印 189/198、九条同名；屏幕全量 finding 1923 → **1922**、**0 新增失败名**、`wpt/css-break` **28 → 27**，其余变化只有 harness 记过的文本抖动（css-lists +144、word-break +7、font-face +1，成对复跑会回到原位）。045 未受影响（它要的是"位移与裁剪"那一半，见 §9.98/§9.107）。

### 9.110 打印口径复核 + 三条候选机制量死（abs 内容重排 / 变换片段裁剪 / 单行 row flex 按 item 分栏）

**打印侧本族已全绿**：`build/reftest-print-score.md` 189/198，九条失败名是 `gecko/pagination/fieldset-001-grid-print`、三条 `wpt/css-cascade/scope-implicit-*-print`、`wpt/css-page/monolithic-overflow-032-print`、`wpt/mediaqueries/scripting-print-script`、`wpt/printing/{input-password,mask-image,select-combobox}-print` —— **没有一条是 css-break 对**。所以目标里"打印口径"这半边对本族已经是满足状态，剩下 27 对全在屏幕侧。（`monolithic-overflow-032-print` 也不在本族范围，且它是"0 像素差、只差页数 7 vs 9"，别当成本族指标。）

本轮把三个候选逐个量到"能写出改哪里"的深度，结论都不是局部改：

1. **`block-end-aligned-abspos-with-overflow`（3000，任务 #12）** —— Chrome 矩形（`build/_bea_probe.html`）：`abs r0 (8,78,20x10)` + `r1 (58,8,20x0)`；`hp r0 (8,78,20x30)` + `r1 (58,8,20x50)`；`gn r0 (58,8,50x50)`。即：abs 盒**自己的高度**只界定它自己的片段，它的**内容**按 fragmentainer 续排；里面 `break-inside:avoid` 的 50 高整块**挪到下一栏**（不是被裁）。Hutu 现在是"整棵 abs 子树重画一遍 + 按栏裁"，所以绿色留在第一栏、第二栏少 20 px 高。要的是逐 fragmentainer 重排（任务 #5 那一层）。
2. **`transform-006/008`（45771 / 19929）** —— Chrome 把每个片段当"自己的盒"旋转（ref 用假栏各给 `.transformed` 自己的高度，origin 在其 bottom-right）。要同时动两处：原点取片段矩形，**并且**裁剪要在**变换前**的元素空间里做（Hutu 是整盒画完后在父空间裁，于是留下的是"旋转后的交集"而不是"交集旋转后的形状"）。差分带三簇（`build/_look-t006.txt`）：`test=#ffff00 / ref=绿黄混` x=271..467 y=50..140；`test=#ffff00 / ref=粉黄混` x=17..269 y=50..127；`test=粉 / ref=白` x=164..353 y=10..48 —— 形状不同，不是单纯位移。
3. **`single-line-row-flex-fragmentation-044`（1000）** —— 不用量 Chrome，ref 自己就是答案：`orange left:50`（留在第一栏）+ `pink left:100`（进第二栏、且 margin-top 被断点吃掉）⇒ **每个 item 保有自己的主流向内偏移，只是被分到不同栏**。现在的 `_flex.Windows` 返回的是 cross 轴分带 `(Offset, Height, Cut)`，表达不了"按 item 分栏"；要给 relays 一条 item 级窗口 + 目标栏的通道（并把 `room <= 0` 的拒绝改掉——本对的容器 `height:0`）。

三条都已立成任务：#12（abs 内容重排）、#14（变换片段的裁剪空间与原点）、#15（单行 row flex 按 item 分栏）。

### 9.111 单行 row flex 按 item 分栏落地：`single-line-row-flex-fragmentation-044` 转绿（css-break 27 → 26）

判据（`ItemBandsForEmptyRoom`）：横排、非嵌套、`column-fill:auto`、`fragmentHeight <= 0`、`Kind == Flex && Flex.IsRow && !Wraps`、子里全是流内块且不带强制断 ⇒ 交出每个 item 沿块轴的带 `[margin-top, margin-top + height]`，按带起点升序。这就是 §9.98.3 当初立的口径："块起点（含 margin）已越过 fragmentainer 末端的项从下一栏起站，起点还在房间内的项留下并溢出"。

一片一个 item：`MulticolChild(offset = 带起点, whole = 容器块轴向长度, relays = true, item = 序号)`，片高 = 带高；**除首片外每片带 `break-before: column`**。第二点是最先踩的坑：不加强制栏断时两片会被同一栏吞下（零高栏的"每栏至少推进一个"规则让它们挤在 x=13），差分从 1000 变 2000。

新的 `item` 通道（`FlexFormattingContext.ArrangeFragment(..., Int32 only)`）在窗口挑成员时只留 `box.Children[only]` 那一个。必须是"同一个布局、每片只画一个 item"，**不能靠裁剪分离**：单行 row flex 里各 item 的块轴向带互相重叠（pink [5,15] 落在 orange [0,20] 里），任何矩形裁剪都分不开 —— 先前给 `MulticolChild` 加 `Lead` 平移裁剪窗的做法（已回退）就是撞在这上面。两个 relay 调用点（窗口片 / portion 片）都把 `child.Item` 往下传，缺一处就会出现"两片都画整条 flex"。

结果：橙（起点 0）留第一栏 x=50 并越过栏底，粉（起点 5）进第二栏顶格 x=100、margin 花在断点上，与 ref 的 `left:50 / left:100` 逐值相同。

三道门禁：单测 **2186**（新增 `FlexLayoutTests.AColumnWithNoRoomTakesTheItemsOfASingleLineRowFlexOneByOne` 钉住这条）；打印 **189/198**、九条同名；屏幕全量 1923 → **1921**、**0 新增失败名**、**0 对变差**（GREW 为空），且 `abspos-in-opacity-003` 在这次全量里仍绿；flex 命名集单模块复跑除 044 消失外一字不差 ⇒ 触发面足够窄。

### 9.112 带块向 margin 的盒也能被栏按单元切开，片各自的矩形成了变换原点：transform-006 45771 → 30376、transform-008 19929 → 10531

三条一起才动（分开都不够）：

1. **`DividesUnits` 不再因 `margin.Top != 0` 拒绝**。旧排除写的是"margin 从首片占掉多少房间这里还没算"——而房间算法早就有了（`AddBoxCut` 的 `headMargin` / `pushed`）。放开后 006 的 `.transformed`（`margin-top:50; height:175`）不再整块站在第一栏，而是切成 4 片，与 ref 的四个假栏一一对应。
2. **片自己的矩形成了变换的 reference 盒**：`FragmentSlice` 加 `Rect? Piece`，两处切片站点把 `rect`/`held` 传进去；`LayoutEngine` 的变换块用 `slice.Piece ?? BorderBox` 解析 `transform-origin`、translate 的百分比与 offset-path，并把 `layout.TransformOrigin` 一律按 `layout.BorderBox` 存偏移（Painter 仍用 `BorderBox + TransformOrigin` 取绝对点，两者不然大就错位）。CSS Transforms 1 §transform-origin 读 CSS Fragmentation 3 §4.1：每片绕自己的 right-bottom 转。
3. **栏给盒的房间要先让出它自己的块向起始 margin**：`room = limit - (used - height) - children[placed-1].Margin.Top`（CSS 2.1 §8.3.1：margin 在盒开始处花一次，不是每片都花）。这条把 006 第一片的裁剪从 100 高修成 50 高，与 ref 的 `height:50` 对上。

门禁（`build/_score-f.md` 对 `build/_score-044.md`）：全量 **0 新增失败名**、0 对转绿/转红，**16 对改善共 62968 px**（除 006/008 外，`gecko/flexbox/pagination/flexbox-{single,multi}-column-1a..1f` 14 对 14450 → 11475）；css-break 仍 **26** 对；单测 2186；打印 189/198 九条同名。

一处已知变差：`gecko/flexbox/pagination/flexbox-single-column-6` 6300 → **9350**（+3050）。该对 §9.72 起就被记为"等更细的 unit/边框带判据"（`8ce5e4c7` 当年也只把它 7600 推到 6300），这次是放开切分后的连带；仍红，不进名字集。

试过并撤销的两条（都无测量效果，别再来一遍）：最后一片的 `painted` 减 `Margin.Vertical`（全量 0 变化）；只对非 relay 片让 margin（0 变化）。

剩下的：006 的 30376 = 第三片该 25 高却仍拿 100（`last`/`content` 用 `Painted` 时连同 `.child` 的 300 高溢出与 `break-inside:avoid` 一起算），008 的 10531 = §9.66 记的"abspos 要站在片自己的包含块上"。

### 9.113 `0ecda5e4` 补了两条单测；顺手量到"空尾片"：`Painted` 把 margin 算进了延伸长度

`MulticolBoxCutTests` 新增两条（单测 2186 → **2188**）：
- `ABoxWithItsOwnBlockStartMarginDividesAtTheRoomLeftAfterIt`：容器 `columns:2; height:100px; column-fill:auto`，子 `margin-top:60; height:120`，内装 200 高的普通子 ⇒ 三片的裁剪必须是 `(0,60,h40) (100,60,h100) (200,60,h100)`（第一栏只拿到 margin 之后的 40）。
- `EachPieceOfADividedTransformedBoxTurnsAboutItsOwnRectangle`：同一形状加 `transform: rotate(25deg)`（不写 origin ⇒ 默认 50% 50%）⇒ 三片的 `TransformOrigin` 分别 `(50,20) (50,90) (50,190)`，即各自那一段的中心；若是整盒中心，三片都会是 `(50,60)`，测试里专门断言第一片 **不等于** 60，防回归到整盒。

顺带量到一个小缺陷（未修，别当成本族阻塞）：把内子换成**不溢出**（`height:120`，与盒同高）时，仍是三片，第三片裁剪 `(200,60,100x40)` 对应盒的行 `[140..180]` —— 盒只到 120，所以这一片**什么都不画**。原因：栏内推进用的是 `Painted(child) = max(Reach, Fragment.Height)`，而 `Fragment.Height` 含 `margin.Top/Bottom` ⇒ 延伸长度被 margin 又算了一遍。试过 `Painted - Margin.Vertical`：对 `css-break` 全模块 0 变化（所以当时被当"无效果"撤了），但它正是这条空片的成因；要修得同时看 `last`/`resumeAt` 的坐标口径（`resumeAt` 是盒内行号，不该含 margin），别只动一处。

### 9.114 竖排表两对与 045 的"缺哪两块"量清了：竖排容器从不切表、片的裁剪是"一片一 clip"而非"一盒一 clip"

**竖排表（`table-grid/row/col/section-paint-vrl/vlr-*` 共 8 对 + `table-collapsed-borders-paint-vrl/vlr-*` 2 对）**：探针 `build/_probe-vtb.txt`（`table-grid-paint-vrl-rtl`）显示 **表的内部轴已经是对的** —— `tablerow 225x80 vertical`（块轴 = x：行自身沿 x 占 225 ✓ Chrome 也是 50+100+75 = 225；行内向 = y 占 80 ✓ Chrome 的 `100 − 表边框 10+10 = 80` ✓），容器 `692,192 100x400 vertical` ✓。缺的是两块：

1. **caption 的内向长度成了 0**：`caption 547,192 140x0 vertical`（x 140 ✓ = `block-size:140`），但 y 应当铺满表的内向长度（Chrome 给 `[0,380 100x100]`，y = 栏内向长度 100）；表的 `inline-size:100%` 走的是所在栏的内向长度，Hutu 的 `TableFormattingContext` 算 caption 的 cross 尺寸时按 `contentWidth`（横排语义）取，竖排下该取内向（y）方向。
2. **竖排容器根本不切表**：`ArrangeMulticolChildren` 给竖排走 `ArrangeMulticolRunAcross`，但没有任何判据会把 Table 子在**块向（x）**切开 —— `DividesAcross`/`DividesUnits`/`RelaysInColumns`/`RowRelaysInColumns`/`BreaksAtLines` 全部显式 `!vertical`；`AddBoxCut` 虽然带 `vertical` 形参并会用 `BlockExtentOf(size, vertical)`，但对有子的表它要求 `ClonesDecoration` 或 `HoldsPositioned`。Chrome 的 4 个 `100x100` 片段（y=380/280/180/80，块轴向 x 各自从 0 起算）说明：竖排的切片是"每栏一块 x∈[0,100] 的窗口 + 续片从栏头重画"，与横排同构，只是轴换了。⇒ 任务 #8 的真实工作量 = (a) 让竖排的分栏判据接受"块向超栏"的子（含表），(b) 表 caption/行的 cross 尺寸按所在轴取；这两条都要跟 8 对竖排表一起量，别只对着 `table-grid-paint-*` 调。

**045（10000 px）**：`build/_oof45_real.html`/§9.98 的 Chrome 矩形说清楚了模型 —— **每个盒子有自己的片段裁剪**：wrapper 的两片在 `[-92,-50 40x100]`、`[-42,-50 40x100]`（未位移），`.rel` 的两片在 `[8,50 40x100]`、`[58,50 40x100]`（位移后），abs 再按 `.rel` 的片段各画一片 `[8,50 50x100]`、`[58,50 50x100]`（宽 50 > 栏宽 40，只被块向切）。Hutu 是"一片一 clip"：clip 挂在最外层片上并作用于整棵子树，所以 `build/_clip-move.patch`（把片的 clip 随片自身 `RelativeDisplacement` 平移）对它无效 —— 位移在里面 `.rel` 上。要修得先有"片内每个被位移的盒各自一条裁剪带"的模型（`LayoutBox.FragmentClip` 目前只能有一个、且由父级 `_cuts` 栈继承），不是再加一次平移。

### 9.115 任务 #8 的第一半落地：竖排容器现在也切表（`table-grid-paint-vrl/vlr-rtl` 各 48980 → 37580）

`AddBoxCut` 的拒绝表里有一条"`BlockSizeIsAuto && 无 min/max && (不是表 || vertical || HoldsPositioned)`"——对表来说它只在**横排**放行（`Kind != Table` 那一项把表除外），`|| vertical` 又把竖排的表挡回去：于是竖排容器里的表永远整块站着，四栏各 100 的块向窗口根本没用上。去掉 `|| vertical` 后，Chrome 量的分布立刻对上：140 高的 caption 出 `100 + 40`，225 高的行出 `50 + 100 + 75`（`build/_probe_vtable.dom`、§9.81）；Hutu 的片现在也是这个分配（`table-grid-paint-*` 两对各减 11400 px）。

门禁：全量 1921 finding **同名集合不变**、**0 新增失败名**、**0 对变差**（只有这两对改善），css-break 仍 26 对；单测 **2188**；打印 **189/198** 九条同名。

#8 剩下的第二半（已定位，下一片）：**caption 的内向长度**。Hutu 现在是 `caption 547,192 140x0`（块向 140 ✓、内向 0 ✗），Chrome 是 `[0,380 100x100] + [0,280 40x100]` —— 内向（竖排里是 y）要铺满表的内向长度（栏的 100）。`MeasureCaptions(TableBuild, contentWidth, ctx)` 与 `ArrangeCaption(caption, x, y, width, ctx)` 都按**物理宽度**传内向尺寸，竖排得换成物理高度；同一条也影响 `table-row/col/section-paint-vrl/vlr-*` 那 6 对（它们现在还没被切，切了以后同样要 caption/单元的内向尺寸跟上）。

### 9.116 重复表头/表脚的第二判据：只有 `break-inside: avoid` 的组才会被复制（`sections-and-captions-mixed-order` 87 → 绿）

`RepeatSections` 过去只看角色号（`RoleOf(row.Group) == 0/2`），于是任何一张带 thead/tfoot 的表被分栏切开时，后面每一栏都再画一份页眉。`sections-and-captions-mixed-order` 的第二栏因此把 "6" 与 "3" 叠在同一处画：像素上是一片同形但更深的绿（test=#007500 对 ref=#008000），87 px 全是同一字符的两层 AA。

Chrome 量出来的判据（`build/_repvar.html`：一栏 100 高，表头/表脚各 20）：
- 组上写 `break-inside: avoid` → 每栏再画一份（A：三栏各有蓝与粉；D：嵌一层栏也一样）；
- 组上不写 → 一份都不再画，只在它自然的行位置出现（B/C）；
- `avoid` 写在组里的那一行上不算，只认组自己（G：无重复）；
- 打印同理：`@page 200 100` 下带 avoid 的表每页一份表头表脚（P），不带的只有自然位置那一份（Q）——见 `build/_repprint.pdf` 逐页取样。

CSS Tables 3 §6.2 原文："user agents must repeat header rows and footer rows on each page spanned by a table **if the page is the table's fragmentainer, if the header/footer has avoid break-inside applied to it**, if the height required to do so is inferior to two quarters of the page height (up to one quarter for header rows, and up to one quarter for footer rows), and if that doesn't cause a row to be displayed twice on that page." 同节末也允许扩到别的分栏上下文："User agents may decide to extend this behavior to more fragmentation contexts, for instance repeat headers/rows across columns in addition to pages."——Chrome 扩到了栏，并把 avoid 这个前提一起带了过去。

同一批量测还给了阈值：一栏 100 时表头 20、25 会重复，26 不重复；一栏 200 时表头 40 会重复，51 不重复——正是"不超过分栏器块向长度的四分之一"。这一条本片没做：`RepeatSections` 手上只有片自身的 `slice.Height`，而最后一栏的片高小于栏高，拿它当分母会把该重复的判成不重复，需要把分栏器的长度传进来（任务 #16）。

代码：`Build` 把举起的那两组记进 `build.HeadGroup/FootGroup`（§3.1 举的只是**第一**组，同角色的后面那些是正文），`SectionBand(build, role)` 换成 `RepeatedBand(build, role)`——只量那一组自己的行（旧代码把同角色的两组一起跨进去，混序表因此量出 220 的假带高），并要求它的 `Style.Breaks.Inside == BreakValue.Avoid`。一处判据喂两边：画的复制（`RepeatSections`）与栏为它让出的房间（`Measure` 记进 `_repeatedBands`，`AddBoxCut` 取用），所以没有 avoid 的表既不再画页眉、也不再为它腾房间。

门禁：全量 **1995** finding（HEAD 基线 1996）、**0 新增失败名**、**0 对变差**、`wpt/css-break` 26 → **25** 对；单测 **2190**（新增 `AHeaderGroupFreeToBreakStandsInNoColumnButItsOwn` 与 `AHeaderGroupThatMustNotBreakIsCopiedIntoTheColumnAfter`）；打印 **189/198** 九条同名。

### 9.117 竖排多栏的两条轴：栏带取最宽一栏，内容从块向起点边往回排（`break-inside-avoid-min-block-size-2` 133840 → 绿）

Chrome 量的（`build/_mb2probe.html` → `build/_mb2chrome.txt`，`vertical-rl` + `columns:3` + `inline-size:500px`，两个块向 40 的孩子）：容器是 **40x500** —— 栏带只有"最宽那一栏"那么宽。Hutu 答 80x500，因为 `MeasureColumn` 把块向孩子一律交给 `MeasureColumnChildren` 沿块向**累加**（`BlockLayout.cs:318`），那里根本不问 `Multi.IsMultiColumn`；横排走的是 `MeasureMulticolChildren` → `MulticolExtent`，取的是最高的那一栏。补上竖排分支（`ColumnsOf` 给栏数与每栏的内向长度，块向可用长度作 declaredHeight，`heightStated` 只在页面真写了块向长度时为真）之后，四组容器的尺寸全部对上 Chrome：auto→40、30→30、60→60。

同一对里剩下的 25600 px 是第二条轴：`ArrangeMulticolRunAcross` 的块向起点取的是 `boxHeight`（内容平衡出来的长度）而不是容器自己的块向长度——60 宽的栏带里放 40 的孩子，起点算成 `content.X + 40` 而非 `content.X + 60`，孩子于是贴到了左边，也就是块向**终点**边。改传 `content.Width` 后 `vertical-rl` 的孩子从右边往回排（`Flow.BlockIsRightToLeft` 只看书写模式关键字、不看 `direction`，这一条本来就是对的）。参考页用 `margin-block-end: 40px` 模拟的正是"孩子站在起点边"，所以测试侧一站对，两页即重合。

顺带记下：横排孪生对 `-1` 之前的绿是**空的**——Hutu 同样把它的 avoid 盒子切成 30 + 10，只是 ref 拿 margin 模拟之后像素恰好重合；这次改动没让它变差（`-1`、`-3` 仍无 finding）。Chrome 在 30 宽的栏带里确实会切开装不下的 avoid 盒子（它的 rect 是跨两栏的并集），所以"切"本身是对的，本对缺的只是摆位。

门禁：全量 1995 → **1994** finding、**0 新增失败名**、**0 对变差**、`wpt/css-break` 25 → **24** 对；单测 **2192**（新增 `AVerticalBandIsNoWiderThanItsWidestColumn`、`AChildOfAWiderVerticalBandStandsAtTheBlockStartEdge`）；打印 **189/198** 九条同名。

### 9.118 竖排碎片的阴影带：块向起点是高坐标（`box-shadow-003` 10860 → 7400）

`BandOf` 过去把 `SlicesBlockStart` 一律对应到 `low`、`SlicesBlockEnd` 对应到 `high`——这对 horizontal-tb 与 vertical-lr 成立，对 vertical-rl 恰好颠倒：竖排的块向起点在**右**边（`Flow.BlockSign < 0`）。于是 `box-shadow-003` 的第一片把阴影留在左边（x=0..57，画到容器之外），而 Chrome 留在右边（x=158..167）。按 `BlockSign` 交换两端之后三片全部对上：band 依次是 58..168、58..158、98..158（`build/_bs3-hutu3.txt` 里新加的 `area/shape/band` 转储直接可读）。

顺带把 `LayoutDumper` 的阴影行补成 `area/shape/band`：阴影画在一个矩形上、又被另一个矩形裁掉，只给 bounds 时图上那条边没有理由可查。

剩下的 7400 px 与阴影无关：参考页用 `position:absolute` 加 `inset-inline-start` 模拟每一片的环，而 Hutu 把这些 abs 子元素的**静态块向位置**摆到了容器块向起点之外（x=158..257 而不是 58..158）——就是已记录的竖排 abs 静态位置问题（Chrome 从右边缘往左长，Hutu 各调用点递的锚点不一致）。那是一条独立的切片，需要单独量，已记进 #18。

门禁：全量 finding 数不变、**0 新增失败名**、**0 对变差**，`box-shadow-003` 10860 → **7400**；单测 **2193**（新增 `AFragmentOfABrokenVerticalBoxKeepsItsShadowAtTheEdgeTheWritingModeStartsAt`）；打印 **189/198** 九条同名。

### 9.119 量清楚但没落：竖排 abs 静态位置挂在块向起点边（`box-shadow-003` 7400 → 0，但门禁不放）

Chrome 量的（`build/_spprobe.html` → `build/_sp-chrome.txt`，八例）：`vertical-rl` 容器里**没写块向 inset** 的 abs 子元素，把**自己的右边**贴到容器的块向起点边、往左长（100x40 容器里的 30x20 落在 `[70,0]`）；容器宽度为 0 时同样成立（`build/_tstprobe.html`：容器 `[766,8 0x484]`、子元素 `[254,8 512x80]`）。`vertical-lr` 与横排本来就对。

实现方式（已验证可行，补丁存在 `build/_abs-static-position.patch`，170 行）：给 `LayoutEngine.ArrangePositioned` / `PositionedLayout.Arrange` 加 `staticFromHigh`，只在**递的是块向起点边**的调用点置真——`ArrangeColumnChildren` 的 out-of-flow 表，以及 `InlineLayout.cs:2005`、`:4152` 不带 `line` 的那次；`PositionedLayout.Horizontal` 的静态分支取 `x = staticX - margin.Right - width`。flex（`FlexFormattingContext.cs:862`）、grid（`GridFormattingContext.cs:233`）、masonry、多栏 OOF（`BlockLayout.cs:5330`）递的是内容盒左上角，保持假——不区分地全局应用会把 `css-flexbox/alignment/flex-align-baseline-column-vert-rl-rtl-wrap-reverse` 弄红。区分之后：`box-shadow-003` **0 finding**（该对只差这一条规则；先前记的“还要 OOF flow-space”其实说的是阴影，已在 `182e9065` 单独落地）、`abs-pos-border-offset-001` 9614→6426、`-002` 22085→14685、`align-self-static-position-002` 33210→28750。

不放行的原因：门禁按**新增失败名**计数，而这一改动揭穿两对**空过**的自造变体——`wpt-variants/text-spacing-trim/text-spacing-trim-colon-001-{chws,halt}-vrl` 从 0 变 6783 px。它们之前绿，是因为摆错的 abs 栏被推到 800 窗口之外、两页都画不出东西；Chrome 用同样的贴边规则且这两对 0 px 通过，所以暴露出来的是**竖排 text-spacing-trim 的栏宽缺陷**（Hutu 的栏 463.36，Chrome 512），与摆位无关。另有一对 `css-anchor-position/position-area-alignment-overflow-001` 变差 14875→15500。

结论：这条与 trim 栏宽一起落（或在其之后）。判据记下：**凡是新红的对，先查它是否本来就在画空东西**（两页各跑一次 `HUTU_PROBE_PAGE`，看盒子是否落在窗口内）。
