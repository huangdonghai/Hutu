# Hutu 分页能力缺口调查（Paged Media）

> **进展（2026-10-30，`dev-paged` 分支）**：本报告之后分页已按 M1–M4 顺序实现；本节记录已落地的切片与最新测量，下面的"调查"部分保留为当时的缺口快照。
>
> | 口径 | 调查时 | 现在 |
> |---|---:|---:|
> | `-print` 逐页语料 | 142 / 162 | **186 / 198**（34 个 `paged` 用例从屏幕模式转入打印模式） |
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
>   **多栏这条已实测更正归因**：`column-balancing-paged-001` 的差异不在页边界——ref 把 3in 的 `block1` 在两栏里各切 144，即 Gecko 的**平衡允许切开 monolithic 子盒**；我们的平衡路径不切（`FragmentChild` 只有高度与断点值，没有"可切"标记），且 `ColumnsOf` 对"嵌套多栏子盒"（`child.Style.Multi.IsMultiColumn`）与"`column-wrap` 下的 spanner"两处显式回退到普通堆叠（源码注释已注明"until it can"）。所以这一对要先补**多栏平衡的 monolithic 切分 + 嵌套多栏**，属于多栏模块本身，页序列只是继承；与 `monolithic-overflow-032` 所需的"巨型分片"不是同一件事。表格重复 `thead`/`tfoot` 已用"从长文档原处取同一段内容再画一次"实现，不需要按页重排（`e7aa3963`、`d21eeb97`）；
>   `box-decoration-break: clone` 不同——每片都要额外吃掉边框/内边距的高度并推动其后内容，属真正的按页重排。实测 `monolithic-overflow-032` 还需要 Blink 的"巨型分片"模型（超高 monolithic 内容所在的一片一直贯穿到内容放完，下一片紧贴其后），该测试自身的注释即写明"expected behavior here is not obvious"，故保持 ◆。`monolithic-overflow-031` 已由 `0fad0a00` 转绿。
> - 非分页特性（打印模式下同样失败）：`@scope`×3、`(scripting)` 媒体查询、表单控件外观、`mask-image`。
> - **fieldset/legend 布局（还剩 3 对，`gecko/pagination/fieldset-*`）**：这些对照是 Gecko 用例，ref 写的是 Gecko 的 legend 模型（fieldset 边框盒下移半个超出量、边框带按 legend 水平范围开缺口）——已按此实现并落地（`c334961d`：legend 提升为块盒、盒/内边距各吃一半超出量、边框开缺口、"内容恰好顶到页边时按页边切"），但只对**声明了自身块尺寸、位于普通块类父盒、不滚动、无 size containment** 的 legend 生效；auto 高度的 legend、列向、滚动容器、size-contained field 仍留在流内（这些交互各自需要独立测量：flex 基线合成、`contain: size` 的固有尺寸、定位 legend 的偏移、bidi 方向）。打印 177→186，本族 12→3 红。余下 3 对：3 个 grid fieldset（legend 是 grid item：fieldset 是 grid 容器时，measure/arrange 都不走块路径的两个钩子，legend 盒会丢失。落点已探明：`GridFormattingContext.Measure`（`:49`）加 inset、`Arrange`（`:93`）里把 `contentY`/`contentHeight` 让出 inset 并在摆放循环后放 legend、`Compute` 的 `plan.Items.AddRange(items)`（`:775`）之前剔除 `IsLegend` 的子盒；改前备份在 `/tmp/Grid.before-legend.cs`）。legend 自身的 break 值按"忽略 break-before、保留 break-after"落地（`6b2842ff`，00B/00C 转绿），legend 单元前不设断点（`21b1a580`，007 转绿）。
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
| M2.4 分片装饰（`box-decoration-break` 等） | ◆ | `monolithic-overflow-032`（需按页重排 + 巨型分片模型）；`monolithic-overflow-031` 已绿（`0fad0a00`） |
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

*本报告只做调查；未修改引擎代码。所有代码位置均以 HEAD `7357d65a` 为准。*
