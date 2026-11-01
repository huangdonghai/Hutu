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





