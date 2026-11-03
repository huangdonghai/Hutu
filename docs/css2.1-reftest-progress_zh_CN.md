# CSS 2.1 正确性推进记录

这篇文档记录 `conformance/reftests/**` 里 **CSS 2.1 基础特性** 的修复进展、优先级判断、环境前提和已知陷阱。
目的：这些结论原先只散在会话上下文里，落一份档以便续接、避免重复侦察。评分机制本身见 [`reftest-scoring_zh_CN.md`](./reftest-scoring_zh_CN.md)。

---

## 1. 当前基线（空载全量，"画对(pair declares)"计数）

> **本表的数字是 2026-10-15 语料清理后那一刻的快照，已经过期。** 最新的读数在 §8（同日早轮）与 §9（同日晚轮）：wpt 由 2259 一路走到 **4157 / 5834**，gecko **1697 / 2471**，单测 **1857**，硬断言 **55**。要看"现在多少分"，读 §9；要复现数字，按 §4 的全量跑法重跑。留旧值在此只为记录那条线的起点。

| 语料 | 基线 | 备注 |
|---|---:|---|
| **wpt** | **2259** | 2026-10-15 全量重测（**5835 对**，约 2 分钟）。历史增量：Ahem +95、BFC 下移 +2、%-margin/匿名透明 +3、flow-root +2、list-item 计数器 +12、min/max-applies-to 族 +33；**语料清理后**（见 §1.1），比率 38% |
| **gecko** | **1300** | 2026-10-15 全量重测（**2471 对**）。历史增量：定高 margin +19、裁剪感知上提 +1、min/max-applies-to 族连带 +2；语料清理后比率 52% |
| 崩溃 / 画不出 | **0 / 0** | **8306 对**全部画出，无超时放弃 |
| 单测 | **565** | `0adcd50c` 起 +4 |

**基准报告**：`build/reftest-score.md`（2026-10-15 全量，含完整通过/失败清单；`build/` 不入库，重跑即可再生——规程见下方全量跑法）。

### 1.1 语料清理（2026-10-15，规模大幅缩小 → 基线变化，非回归）

语料经过两轮清理：
1. **去 JS 依赖用例**：参考页含 `<script>`/`testharness`/`reftest-wait` 的 **42 对**（84 文件）整体删除。
2. **严格 Chrome 校验**：用本机 Chrome（无头、800×600、抑制滚动条）逐对渲染 test/ref 做像素比对，凡不满足所声明 `match`/`mismatch` 的整对删除——**5581 对 / 11162 文件**（wpt 4290、gecko 1290）。判据=单个 Chrome 版本（用户口径：只兼容官方规范 + 本机 Chrome 实作）。

- 合计移除 **5623 对**；剩余 **wpt 5835 对 / gecko 2471 对**（html 约 1.67 万，原 2.79 万）；删除后的空目录已清理（75 个）。
- 校验器：`build/_chromecheck`（**多进程分片**并行，因单个 Playwright 驱动会串行化所有命令；gitignored 一次性工具）。
- 记分从 wpt 2922 / gecko 1853 变到 wpt 2259 / gecko 1300，是**语料变小**造成的绝对数下降（比率：wpt 38%、gecko 52%）。

**全量跑法（建基准用，平时回归仍用 60 样本）**：临时把 `ImportedCorpusTests.cs` 的 `Listed` 改成 200000 → 构建 → **不设** `HUTU_SCORE_LIMIT` 跑 `TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay` → 存报告 → **把 `Listed` 还原成 60** 并 `git diff` 确认干净。本机实测约 2 分钟（**8306 对 / 16694 html**，半核并行）；`build/score-<corpus>.log` 有逐页进度可轮询。

复现方式（判定回归的唯一仲裁是这套浏览器真相）：

```powershell
# 抬报告上限取权威聚合数（默认 Listed=60 会截断 finding 列表，但聚合行不被截断）
# 临时把 src/Hutu.Conformance.Tests/ImportedCorpusTests.cs 的 Listed 改成 200000
$env:HUTU_SCORE_LIMIT='99999'
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj `
  --filter 'FullyQualifiedName~TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay'
```

> ⚠️ 全量计数受机器负载影响，须**空载**跑；跑完记得把 `Listed` 还原成 60、`git diff` 确认干净。

---

## 2. 环境前提：Ahem 字体必须装到系统层（关键）

- 大量 `wpt/css-text/**`、以及许多 box/table/list 用例通过 `<link href="/fonts/ahem.css">` 引用 `@font-face: Ahem`（每字 advance = 1em 的方格字）。
- **借用语料导入器只拷 raster 图片，不拷字体/CSS**，所以仓库里没有 ahem 文件。
- 引擎的 `SkiaTextMeasurer` 按 family 名解析**系统字体**——因此 **Ahem 需安装到操作系统**。装好后 wpt 从 2775 跳到 2870（**+95，零代码改动**）。
- 验证点：`text-align-start-001` 的 "TESTI" 应测得 `125×25`（5 字 × 25px）。

**推论（重要）**：没装 Ahem 时，`css-text` 族的差异大多是**字体度量噪声**而非布局 bug，改布局代码不可胜；装了之后，残余的 text 差异才是真正的布局/换行逻辑，才可作为目标。跑全量前务必确认 Ahem 已装，否则基线偏低且会误判整族为"不可胜"。

---

## 3. 本轮已完成并提交

### 3.1 `84b646ce` — 定高容器吸收末子底边距
- **根因**：`height` 明确（定尺）的容器，按 CSS 2.1 应把最后一个 in-flow 子元素的 bottom margin **吸收**进固定区，不让它穿透容器下缘推挤后续兄弟。（即"给容器设 height 阻断 margin 折叠"这条经典规则。）
- **改动**：`BlockLayout.CollapsedEnd` 增加门禁——`DeclaredBlockExtent(box, flow, basis) is > 0` 时只返回盒子自身的 bottom margin，不再折叠穿过多子的底边距。
- **门禁**：`block-fix-height-last-child`（14 例）全翻转 → **gecko 1831→1850（+19）**，wpt 持平，0 崩溃，553 单测绿。

### 3.2 `ab2def6d` — 定宽 BFC 盒装不下时下移到能容纳的带
- **根因**：`overflow:hidden` 这类建立 BFC 的盒（floated 侧栏 + 内容的经典两栏）按 CSS 2.1 §9.5 不得与浮动重叠；引擎原只会横向 shift 塞进浮动旁剩余空间，剩余宽度不够时把它推出容器右缘溢出，而不是下移到下一行。
- **改动**：`FloatTracks` 加 `FitBelow`（复用浮动已有的 `NextBottom` 逐级下探）；`FormattingRoot` 对**声明了宽度**的 BFC 盒算出 `drop`，Measure 的 cursor 与 Arrange 的 rect.Y **同步**加 `drop`。auto 宽盒保持原"横向缩短"行为不变。
- **关键坑**：判"装不装得下"的宽度必须是 **border-box、不含水平 margin**（尾随 margin 不参与，否则 `gecko/floats/bfc-displace-4` 的 `margin-right:100 /* ignored */` 会被误判下移）。含 margin 时净 0，去掉后 **wpt 2870→2872（+2）、gecko 持平、0 崩溃、555 单测**。

### 3.3（前序，非本轮 CSS2.1，但在同一条线上）
- `b87cd725`：grid-lanes 交叉轴对齐 + 定宽元素不再被拉伸（wpt +32，属先进排版，已按指示暂缓）。

### 3.4 `0adcd50c` — min/max-applies-to 族：used height 百分比、浮动让位、行尾空格（三处耦合）
- **背景**：交接文档把 `max-height/max-width-applies-to-017/018` 列为"可能静态可解，先查规范"。侦察发现 3 个失败 pair 其实是**三个独立小根因**，逐一查规范后修复：
  1. **浮动不避让前兄弟的折叠底边距**（`max-height-applies-to-018`、`max-width-applies-to-018`）：deep-look 显示浮动盒尺寸**本来就夹紧对了**（100×100），只是位置高了 17.92px（`<p>` 的底边距）。`PlaceFloat` 两阶段都传裸 `cursor`，漏掉 `pending` 条。规范 §8.3.1：**浮动与任何盒不折叠边距** → 前兄弟折叠出的底边距条立在浮动之前，浮动外顶边落在其下（即下一个 in-flow 盒应占的位置）。改：两阶段均传 `cursor + pending`。
  2. **百分比高度对错矩形 + 夹紧时序**（`max-height-applies-to-017`）：树显示 inline-block 父被 max-height 夹成 100 高，但子 `height:100%` 得 0。两个错误叠加——(a) `MeasureRow`/`Arrange` 把**父盒自己的 context**（包含块是祖父盒）传给行内布局，行内原子盒的百分比对到了祖父盒高度；(b) §10.7 的夹紧发生在测内容**之后**，子的百分比没对着夹紧后的 used height 解析。规范 §10.7：tentative 超 max-height 时**用 max-height 作为 computed height 重跑**"上面的规则"（§10.5 百分比对包含块高解析）→ used height 先定、内容对着它测。改：声明高先夹紧再测内容（clamp-before-measure，块子路与行内路都过）；`InlineContext` 新增 `Atomic` 字段——行内原子盒的包含块是**线所在的块容器**即父盒自身内容盒（§9.1.1），与 text-indent 等行内长度所对的"盒的包含块"（§16.1）分开传，两者在 shrink-to-fit 盒上不是同一个矩形。
  3. **行尾折叠空白计入 intrinsic 宽度**（017 的另一半）：剩余 400px 差异是绿方块右侧 4px 红条——源码里 `<span>` 后的换行空白把 shrink-to-fit 父盒撑宽了 4.38px。规范 §16.6.1 步 3：**行尾空格**（white-space: normal/nowrap/pre-line）与行首一样**移除**。行 builder 早有 `TrimTrailingSpaces`，只缺内在测量路径。改：`MeasureIntrinsicCore` 把可折叠空白宽度挂起（`trailing`），有内容跟随才计入 max-content；行首跳过（原 `sawContent` 门）与 preformatted 直出保持不变。
- **门禁**：3 个目标 pair 全翻绿（deep-look "the two agree"）；**565 单测（+4，逐一用 stash 验证无修复即失败）**；60 样本回归与基线一致（gecko 54/60、wpt 45/60）；0 崩溃；0 linter。
- **全量净效果**（2026-10-15 建基准时实测，见 §1）：**wpt 2889→2922（+33）、gecko 1851→1853（+2）**。+33 中 3 例是目标族，其余主要来自行尾空白修复——HTML 源码里行尾空白无处不在，任何含 trailing whitespace 的 shrink-to-fit 盒（浮动/inline-block/表格/绝对定位）此前都被撑宽一个空格。
- **遗留**：`max-height-separates-margin` 等近亲仍属 §5.2.1 的"测量感知折叠"族，未动；`MeasureColumn` 的行内路仍是旧 context（写模式路径，本轮未碰）。

---

## 4. 工作流（每个特性族一轮）

1. 从 `build/reftest-score.md` 按特性族聚类（路径 2/3 段 + 关键字），按"单一根因 × 用例数 × 同质性"选目标。
2. 读代表例的 test/ref，精确反推规则。
3. `HUTU_SCORE_LOOK=<子串>` 驱动 `LooksAtOnePairClosely` 做单对 deep-look：输出两侧布局树 + diff map，拿到**确切几何**再动手。
4. 改代码 → 加/改单测（`src/Hutu.Tests`）→ `dotnet test src/Hutu.Tests`。
5. **回归门禁（用户明确指示，2026-10-28 起：没有"60 固定样本"这一说了）**：一律跑**全量基线**，并且比的不是分数而是**失败集合**——改动前后各跑一次 `TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay`（`HUTU_SCORE_LISTED=9999`，实测约 2 分钟 / 8306 对），把两份 `build/reftest-score.md` 的 finding 路径取差集，`修复 N / 回退 M` 一起报，**M 必须为 0**，否则逐条解释。脚本 `build/_scorediff/diffscore.ps1`（读上一份基线的 CSV 与当前报告，按目录归类修复项）。跑前先确认 `dotnet build` 真的成功了——`--no-build` 配上失败的构建 = 读上一个版本的 DLL，今天的假"基线读数"就是这么来的。
6. 语义化 `fix(css):` / `feat(css):` 单独 commit。**永不主动 push**（远端推送由用户触发）。
7. 门禁不过（净负或平局）→ `git checkout` 回退到干净基线，WIP 存 stash 备查，如实汇报。

---

## 5. CSS 2.1 族菜单与可胜性（post-Ahem）

### 5.0 残余全景（2026-10-15 全量基准，9153 条 finding 按规范模块分桶）

数据源：`build/reftest-baseline.md` 的 Findings 段；复算方式（按语料后第二段取模块名聚类）：

```powershell
Get-Content build/reftest-baseline.md -Encoding UTF8 |
  Where-Object { $_ -match '^- `[^/]+/([^/]+)' } |
  ForEach-Object { $Matches[1] } | Group-Object | Sort-Object Count -Descending
```

| 桶 | 数量 | 占比 |
|---|---:|---:|
| **CSS3+ 模块** | **7764** | **84.8%** |
| CSS 2.1 时代（基础口径内） | 1195 | 13.1% |
| 杂项 / 非 CSS（SVG、UI、harness、pixel-rounding 等） | 194 | 2.1% |

- **CSS3+ Top**：css-grid 1203、css-break 927、css-text 576、css-gaps 482、css-overflow 345、flexbox 合计 454、css-sizing 244、css-shapes 203、css-transforms 合计 254、multicol 合计 230、text-decor 合计 252、css-color 161、css-contain 146、css-counter-styles 140、css-inline 121、css-masking 107、css-page 系 163、css-variables 101、css-anchor-position 99、writing-mode 合计 188。粗分族：Grid 系 ~1723（扣暂缓的 grid-lanes 656 后 ~1067）、分页/断框系 ~1320、文本系 ~1063、Flexbox 系 ~492、尺寸/溢出系 ~735、颜色/背景/边框系 ~525、计数器/列表系 ~239、书写模式系 ~192。
- **分桶口径（判断项）**：`gecko/bugs`(462)、`gecko/text`(57)、`bidi`(59)、`counters`(70) 归 2.1 时代（内容是 §8/§9/§12/§16 规则，只是托管在 gecko 语料）；`css-counter-styles`/`css-lists` 是 L3 模块名，但失败主因是 CSS 2.1 §11/§12 的 `counter()/counters()` 生成内容引擎（见 §5.5），算在 CSS3+ 桶里、攻它时按 2.1 基础算。
- **结论**：残余 84.8% 是现代 CSS，与"先弄 CSS 2.1 基础"一致；基础口径内可攻残余 ~1195，较同质的簇：表格 ~83（anonymous-boxes 34 + width 31 + bordercollapse 19）、float 78、计数器 70、margin 折叠 54、first-letter 35、forms 60、bugs 462（异构回归例，需逐条看）。

### 5.1 推荐先打（纯几何、同质、CSS 2.1 核心）
- **`wpt/css-lists`（110 例，仅 ~172K px ≈ 平均 1.5K/例）**：全是**近失**，marker 定位用 Ahem 方格后应大面积翻绿；列表标记是 CSS 2.1 核心。最像"一处根因翻一片"的大族。**（下一个首选目标）**
- **`wpt/CSS2/normal-flow`（16）+ `CSS2/tables`（9）+ `CSS2/visuren`（9）**：纯几何、最基础，很多已因字体对等而逼近。

### 5.2 margin-collapsing 残余（58 例，已不同质，偏深）
- **负边距 self-collapse**（`block-no-content-2e/3b`）：相邻 run 正负边距折叠算术，逐例不同。
- **测量感知的 min/max-height**（`{min,max}-height-separates-margin`、`margin-collapse-min-height-*`、`block-zero-min-height`）：规则成立**取决于 used height 是否真的钳住内容**，而 `CollapsedEnd` 是纯样式静态判定——要做成测量感知，比定高那条更侵入。
- **`block-xhtml-root` / `table-caption`**：耦合 DOM 解析 / 表格子系统。

#### 5.2.1 normal-flow 的 min-height 折叠族（已侦察，静态改法净负，未做）
- 目标（`wpt/CSS2/normal-flow`）：`min-height-separates-margin`、`margin-collapse-min-height-001/002`、`max-height-separates-margin`、`*-through-percentage-height-*`、`child-bottom-margin-in-unresolvable-percentage-height`（~7 例，首差都在 x=8，纯竖直堆叠）。
- 规范 §8.3.1："bottom margin of a box with **height auto AND min-height zero** collapses with last child" —— 这是**充分条件、非充要**。真实语义看 **used height**：min-height 只有**把 used height 顶到内容之上（active）**时才吸收末子底边距；若内容已 ≥ min-height（min-height 未生效），used=content，边距仍应照常折叠穿透。
- **已试（静态门：`CollapsedEnd`/`IsSelfCollapsing` 里 `min-height>0` 即阻断折叠）**：修好 2 例（`min-height-separates-margin`、`margin-collapse-min-height-001`，都是 active min-height），但**打断 11 例 gecko**（inactive min-height 被误阻断穿透）→ **wpt +6 / gecko −11，净 −5**，已回退（HEAD 仍 `22c2ffa0`）。
- **正解 = 测量感知**：折叠判定需知道本盒 content 高（`cursor`）与 min/max-height 的大小——`CollapsedEnd` 是父层调用的静态助手、拿不到本盒 content 高，改成测量感知要重构 Measure/Arrange 的边距回传（侵入大、易回退）。`max-height-*` 与百分比高度族同理需此路。`margin-collapse-min-height-002` 另含 marker 定位近失（1476px，与本门无关）。
- 结论：此族**不是**能靠静态规则干净拿下的，暂缓；若做需专门一轮做测量感知折叠。

### 5.3 暂缓 / 不可胜（明确排除）
- **`white-space` 残余多是 `break-spaces` / `pre-wrap`（CSS Text 3，非 2.1）**——按"先弄好基础"的口径暂缓。
- **`floats` / `floats-clear`（CSS2 里约 65 例）**：异构多根因，非一处能翻一片。已收（`ab2def6d`）：**定宽 BFC 盒装不下时下移**（+2）。剩余子因待攻：**`floats-placement-*` 疑似浮动背景未绘制 / paint 层序 bug**（float 布局矩形对却画成容器底色——若是 paint 问题影响面大、优先查）；**`new-fc-separates-from-float` 的 BFC↔浮动 margin 分离**；**`*-clearance` / `clear-*` 的 clearance+顶边距算术**；**`floats-clear-multicol-*`（耦合 multicol）**。早期"仅 BFC 容器才包含浮动"的核心不变量修复实测净 −14（谓词漏 flex/grid item + 需跨块 band 折行），已回退。

#### paint bug 已修复（`158173d5`）
`floats-placement-001/002` 的多余红 fill 根因是**生成式匿名块重复绘制父 background**（BoxBuilder 用 `parent.Style.AsAnonymous()` 造匿名块，保留了 background），因“子按文档序绘制”盖住了先画的浮动。
- **修复（两件耦合的事，一起提交才净正）**：
  1. `Painter.PaintBlock` 对 `box.Source.IsAnonymous` 跳过 background/border（匿名块非元素，只画子）——placement-001/002 翻绿。
  2. 但单独上它会回退 `gecko/bugs/249982-1`：它**揭开一个预存的浮动 shrink-to-fit bug**——auto 宽 + `margin-right:40%` 的浮动把百分比边距算在收缩后的可用宽上而非包含块（得 188.85 应 58.75）。`BoxModel.ResolveWidth` 新增 `containingWidth` 参数：百分比的 width/margin/min/max **一律按包含块解析**，`basis` 只用于 auto 填充封顶；普通块两者相等、无变化（flex/grid/masonry 三处调用同步传 context 宽）。
- 门禁：**wpt 2872→2875（+3）gecko 持平 1850（0 崩溃，556 单测）**。
- **残留**：`gecko/css-grid/grid-item-sizing-percent-002` 仍失败——透明揭开的是另一个**网格项 background/`background-clip:content-box` 的独立 bug**（与浮动 %-margin 无关，需单独查）。

#### （历史）早期误判更正
之前猜测 IsAnonymous 一刀切会误伤 block-in-inline 携带 inline 背景——错：本引擎只有 WrapInline 两处 IsAnonymous，无 block-in-inline 匿名机制；真实原因是浮动 shrink-to-fit 与网格项 bg 两个预存 bug 被遮盖。
- **grid-lanes / masonry（先进排版）**：用户已指示"先进排版先不弄"，M2 最短车道 WIP 存于 stash，M4/M5/M6 未做。

### 5.4 `display:flow-root` 已支持（`afdad340`）
flow-root 现在建立 BFC（装住子 margin + 与浮动 stand-clear），wpt 2875→2877（+2）。做法：AngleSharp 无 flow-root 语法会丢弃它→降为普通 block；`WideKeywordDeclarationFactory.KeywordSubstringValues["display"]` 加上 `"flow-root"`（与 grid-lanes/masonry 同机制，AnyTokenPassThrough 保留原词）；布局层 `EstablishesContext`/`FormattingRoot` 读 `Display.Contains("flow-root")`。注：无需改 `ComputedStyle` 的 display 取路径（fused 路径 KeywordText 读 declared 已能拿到保留的令牌，已隔离验证）。
- 残留：`floats-wrap-bfc-with-margin-004/005/010`、`nested-clearance-new-formatting-context`、`floats-bfc-003`、`new-fc-*` 等仍失败——flow-root 建立后它们的残余错位是**另外的机制**（BFC 自动宽 + margin-left 旁的浮动定位、父高/清除交互），非单纯建立 BFC 可解。
- 其它残余浮动子因：clearance×大顶边距×父覆盖（`*-clearance`/`clear-*`，每例不同、易回退）、block-in-inline 拆分（`float-in-inline-*`，大块）、multicol×float（`floats-clear-multicol-*`）——均多因素耦合，非单一干净根因。

### 5.5 `display:list-item` 本体 OK；css-lists 残余靠 CSS 计数器引擎（`b6b0dcc8` 已推进一步）
- 验证：`display:list-item`（任意元素、`<li>/<summary>` UA、disc/decimal、inside/outside、::marker）基础都通过；css-lists 110 失败集中在高级计数器特性。
- **已做（`b6b0dcc8` +6、`cd683c12` +6，共 +12）**：marker 编号走真正的 `list-item` 计数器——`ListArea` 新增非继承的 `ListItemReset/Set/Increment`（两条样式路径都从自身声明解析 `counter-*` 里的 `list-item` 项）；`ListMarkers.Number` 重写为**遍历容器全部子节点**的步内模型：非项元素（`<div>` 等）带 `counter-increment/set/reset: list-item` 会推动计数但不占号；`value`/`counter-set`/`counter-reset` 为绝对锚点；reversed 无 start 时布局期自定起始值使**末项=1**（多步增量会带动其后所有号），有 start 则首项=start。基础 `<ol>`/`reversed`/`start`/嵌套行为不变（无回归）。
- **残余（css-lists ~98、li-value-reversed ~25）**：主因是 **嵌套 `counters(list-item,".")` 显示**与部分 reversed×多步×start 的组合边角（需 `content: counter()`/`counters()` 函数与完整作用域，属 CSS 2.1 §11 生成内容，未实现）；余为 `@counter-style`/string 等。完整作用域计数器与 `counter()`/`counters()` 是下一步（独立一轮）。

---

## 6. 关键代码位置

- margin 折叠：`src/Hutu/Rendering/Layout/BlockLayout.cs`
  - `CollapsedStart` / `CollapsedEnd`（盒子向父/兄弟呈现的块轴外侧边距）
  - `CollapsesThroughStart` / `CollapsesThroughEnd`（子边距是否穿透本盒计入本盒高度）
  - `MeasureBlockChildren`（行内堆叠 + 尾随边距 + 浮动长高）
  - `EstablishesContext`（BFC 判定）/ `IsWall`（establishes 或 out-of-flow 或 float）
  - `Combine`（正最大 + 负最小的边距折叠）
  - `DeclaredBlockExtent`（块轴声明尺寸，定高判定用）
- 单测：`src/Hutu.Tests/LayoutEngineTests.cs`（`AFixedHeightParentAbsorbsItsLastChildsBottomMargin`）、`FloatLayoutTests.cs`。

---

## 7. 待办 / 下一步

- [x] **表格部分（及其后代）的强制断点已传播到表格边界**（`aaf85ea9` + 单测 `594457e2`；`wpt/css-break/table/break-{after,before}-table-cell{,-child}.html`，4 对同签名 `10000 像素 / 首差 58,50`）。规则：栏无法在表格的零件之间结束，所以写在 cell（或 cell 里的子盒）上的强制断点在表格内部无处可落，就归到表格自己的边界，并保持它原本那一侧（`break-after` 出在表格的后面）。与 `avoid` 的冲突由现成的 `Fragmentainer.Combine` 裁决（强制 > avoid）；表内的 `avoid` **不**外传，因为它说的是表格自己决定的那个断点。裸 wrapper 不走这条路——切片器早已把 wrapper 拆进容器的成员里（那正是"断点落在它被写下的位置"的机制）。
  - 落点：`BlockLayout.HoldsForcedBreak`（决定"要不要走多栏"）与 `MulticolChildren` 里造 `FragmentChild` 的两处（决定"断在哪"），共用新的 `BoundaryBreaks(child)` 读表格整棵子树。
  - 门禁：`css-break` 模块 86 → **90**（其 204 对内）；全量 wpt 4160 → **4164**、gecko 1697 持平，失败集 0 回退。单测 1857 → **1858**。测试按"同一页写两遍（有/无 cell 上的断点）求差"，且在修复的父提交上验过会红（症状正是 after 留在栏首而不是进下一栏）。
  - **这一族剩下的**：`css-break/table` 还有 36 对红，签名分三堆——`table-*-paint-*`（12 对，跨栏的绘制与边框在边界上的绘制）、`table-fragmentation-001/002*-print`（8 对，分页时表格分片与 `@page` 尺寸）、`repeated-section/*`（约 10 对，重复表头/表尾与内嵌多栏）。它们要的是"表格真的跨 fragmentainer 切片"这个能力，不是这条传播规则；`TableFormattingContext` 目前完全不读 `Breaks.*`，也不接 fragmentainer 的分片接口。另：`specified-block-size-007`、`table-border-007/008`、`sections-and-captions-mixed-order`、`table-caption-and-cells*` 也在这族里，症状各异，别当一条打。
- [x] **`text-emphasis-position` 的 8 对已收**（`005d/e/f/g`、`006d/e/f/g`，签名同为 `739 像素 / 首差 51,50`；005 系"该有号处画白"、006 系"该白处画了号"，两两镜像）。矩阵核实过：16 例全带 `text-orientation: sideways`，`vertical-rl` 那 8 例原本全通过、`vertical-lr` 这 8 例全失败。
  - **两条被否证的猜测留此备查**：(a) "Side 在 vertical-lr 下换了线的哪一边"——把通过的 005（rl）与失败的 005d（lr）各跑一次 deep-look，两页布局树、行盒、5 个符号坐标逐字相同（都在 x=88）；(b) "sideways 抬高基线"——两页 base 分别 33.91 与 41.91，差的 8 恰是符号带 `font/2`，与 sideways 无关。当时还以为是"预留带被算进正文的横向居中"，那也是误读：run 宽 88 vs 参考页 80 的差在参考页里是由 5 个 ruby 基各自占位造成的，与本症无关。
  - **真根因（`a98bc0ef` 已修）**：符号预留带按"线的哪一头"扣，而不是按"符号站在哪一边"扣。`vertical-rl` 与 `vertical-lr` 的转向是同一个（顺时针 90°；只有 `sideways-lr` 这个模式名才逆转），差别只在列往哪边走，所以正文"向右够出的那份"在两个模式里都是 ascent。符号在 `right` 时永远该从 ascent 扣——但在 `vertical-lr` 下 block-end 恰好吃掉同一条右边缘，旧代码 `marks.StandsAtTheEnd(flow)` 于是把它当作 under、从 descent 扣，共享基线落进符号带内部（`base 41.91` 而非 `33.91`），字形压在自己那串圈上。rl 那 8 例通过，是两处错处正好相消。
  - **改法**：新增 `TextEmphasis.KeepsItsStripOnTheAscentSide(flow)`——横排仍只看 `over/under`（横排里 `left/right` 本就不说话），竖排只看物理的 `Side` 配 `flow.TurnsCounterClockwise`；预留处 `src/Hutu/Rendering/Layout/InlineLayout.cs:5652` 换用它，符号的实际绘制位置（`Marks` 里那句 `StandsAtTheEnd`）不动，因为它原本就画对。单测 `AMarkedColumnKeepsItsStripOnTheSideTheMarksStand` 把 rl 与 lr 成对量（尺子是另一个模式，不是从带子上读出的数），并核 `right -> base 17`、`left -> base 22`、列宽 45、两条带分别在 x=40 与 x=0。
  - 门禁：`css-text-decor` 模块 111 -> **119**；全量 wpt 4149 -> **4157**、gecko 1697 持平，失败集 0 回退。集合里唯二的差异是 `css-ruby/ruby-align-002`（负载抖动，单跑 2/2）与一条 `could not be drawn`（`WideKeywordDeclarationFactory._cache` 的并发 race，见下一条；该页本来就列在旧的失败清单里，单独跑 `css-sizing` 通过）。单测 1856 -> **1857**、硬断言 55 全绿。
  - **这一族剩下的（截至 `be6c1a29`，只有 3 对）**：`text-emphasis-ruby-001/002`（984 / 1651 像素，符号要画在 ruby 之外——参考页用嵌套 `<ruby>` 把符号垫到读音的外侧，我们的符号带与读音带打架）；`text-combine-emphasis`（pair 声明 mismatch、我们画成同一张图）。原先记在这里的 `001/001a-c`、`002/002a-c`、`punctuation-1/2`、`016a~020a` 已随 `a98bc0ef` / `84044961` / `be6c1a29` 三处修复翻绿（前两组是被"预留带按哪一边扣"与"未标记字符照付宽度"顺带修掉的）。
- [x] **`position-relative-010/013`（含 011/012）已收**（`7f1a51e7` + `28d9e04e`；`wpt/css-position/position-relative-010..013.html`，整族 6 页 008~013 现已全绿）。两半各是一处独立缺陷，先用探针把数字钉下来，再分别修：
  - **(a) 格子的内容对齐把"绘制位置"当成了"流内位置"（`7f1a51e7`）**：`TableFormattingContext.AlignContent` 用子盒**当前**的 `BorderBox` 量剩余空间，而 `position: relative` 的语义是"占位不动、只挪绘制"（CSS 2.1 §9.4.3）。于是它把偏移当成"内容不够高"的证据，先移走一半（free=(100−90)/2=5），通用路径再按声明移 10 → 净 −5。修法：给 `LayoutBox` 加 `RelativeDisplacement`，在**两处**平移的入口记录（`LayoutEngine.Arrange` 与 `PositionedLayout.Translate`，后者是 inline 元素分片的移动口），对齐时减回去。实测：`top:-10px` 从 −5 变 −10、`top:10px` 从 +5 变 +10。这一半单独把 011/012 带绿（css-position 35→37）。
  - **(b) 百分比偏移按 used 高求解（`28d9e04e`）**：010 要求"父未声明高就移动 **0**"、013 要求"行声明 10 就移动 **10**"，我们两种都移动 100（行被格内 100px 的 div 撑出来的 used 高）。走的是 §7 原本评估的 **(b) 路线**（表格侧自算位移、阻止通用路径二次平移），而不是把声明高塞进格子 CB 的 (a) —— 后者会连带改变格内子元素百分比高度的 `definite` 判定。实现：`Arrange` 增加可选 `Offset? displacement` 参数，表格传 `PartOffset(cell.Box, grid.Width, StatedHeight(row.Box, 0))`，通用路径**取用而非相加**，因此只平移一次。跨行 `rowspan` 取起始行、横向百分比取该行跨的网格宽（语料无此类页，按 §10.5 同读法处理）。
  - 门禁：css-position 35 → **39**（其 67 对内）；`css-tables`(84)、`CSS2/tables`(7) 作为连带闸门**读数未动**；全量 wpt 4169 → **4173**、gecko 1697，失败集 0 真回退。单测 1859 → **1861**、硬断言 55 全绿。两条单测都按"同一页写多遍求差"，并在**没有对应修复的版本**上验过会红、且红在规范点名的数字上（(a) 红成 ±5，(b) 红成 100）。
  - **教训（老坑重踩，务必记住）**：验证 (b) 时用 `File.Copy(backup → work)` 还原源文件，`Copy` 会**保留备份的旧 mtime**，MSBuild 判定"不必重编"于是 `--no-build` 跑的是上一次回退留下的**旧 DLL**，报出一条假失败（"移动了 100"）。规程：还原后必须把源文件的 `LastWriteTime` 设为当前时间，或显式 `dotnet build`，再跑测试；并用 `Get-FileHash` 核对还原内容与动手前一致（本次 `True`）。另：全量运行在负载下会把无关页打成"画不出/超时"（本轮见 `css-flexbox/align-self-015`、两条 gecko 页），凡集合里新增的项先单独复跑再判定。
- [x] **"Drop ones"（`fa84e87d` 已收）**：中文书写式里"十"的那个"一"何时省。规范 §7.1.1.3 的字面（"any group with a value between ten and nineteen"）会把 `10011` 也省掉，而 gecko 参考页写作 `一万零一十一` —— 两簇语料合起来的证据是**问"十在本组里是不是孤零零"**：组内个位为空（或本组是首组）才省。据此 11 → `十一`、10010 → `一万零十`、100000 → `十万`、10011 → `一万零一十一`、11111 → `一万一千一百一十一` 全对。门禁：1852 单测 / 55 硬断言全绿，全量 0 回退、+3（两簇 informal + `stretch-input-in-column`）。
  - **残留（下一步）**：规范其实按语言给了三条不同的省略规则 —— 中文只看 10..19 的组，日文/韩文是"任何数字标记前那个不是组内首位的 1 都省"。现在 `UnderTenThousand` 三种语言共用一条判据，靠 `Short`/`WritesZero` 间接区分；`Counting` 该加一个显式的省略策略字段，把日文/韩文那条真正实现出来，而不是让中文的规则顺带决定它们。语料目前覆盖不到日文/韩文的这类值，所以这次没暴露。
  - 走过的弯路留此备查：(a) 把条件改成"该段读法的起始" → wpt +2、gecko −2；(b) 想拿 Chrome 伪元素的 `getComputedStyle(::before).content` 取证不可行，Chrome 不消解 `counter()`，直接返回字面量。要实测走 `build/_chromecheck` 的逐像素校验。
- [x] **表部分的 `position: relative` 偏移没有生效**（`wpt/css-position/position-relative-table-*`，**16 对**，签名齐整：普通版 `5000 像素 / 首差 8,8`，`-absolute-child` 版 `8,58` 或 `58,8`）。已把页面与参考页的数字核清：
  - `thead`/`tbody`/`tfoot`/`tr` 写 `position: relative` 加 `top`/`left: 100px`（`-absolute-child` 版是 50px），单元格 50x50，`.indicator` 是绝对定位的红色方块、落在同一坐标；参考页把那个位置直接画成绿色，也就是"被相对偏移的表部分正好盖住标记"。我们现在完全不平移，红色露出来。
  - 落点 `TableFormattingContext.ArrangeRows`（`src/Hutu/Rendering/Layout/Table/TableFormattingContext.cs:224`）：那里只用 `PositionedLayout.IsPositioned(row.Box)` 与其 group 决定**绝对后代的包含块**，从未调用 `PositionedLayout.RelativeOffset` 平移盒子本身。同文件已有 `Shift(box, dy)` 可复用（它已跳过 out-of-flow 子树）；组与行都写偏移时按组→行叠加。
  - **耦合点（漏了它，8 个 `-absolute-child` 仍会红）**：给绝对后代的那个 anchored Rect 必须是**平移之后**的位置 —— 那几页要 `thead` 移到 50、其内部 `top:50` 的绝对子元素落在 100（=50+50）。所以 Rect 要在偏移应用之后再算，或一并加上同一个偏移。
  - 规范依据：CSS Positioned 3 §6.2 把表格部分按块框处理，相对偏移适用；本文件第 271-279 行注释里那句"表格部分的 position 由 CSS 2.1 留白"讲的是**包含块归属**，不能当作"不平移"的理由。
  - **已收**：`ArrangeRows` 现按 `PartOffset` 读表部分自己声明的相对偏移（行叠在它所从的组之上，两者都对表格内容框求解），并用 `MoveWithContent` 把整行连同其绝对后代一并移动 —— 那些 `-absolute-child` 页要的是"行动了，行内的绝对子元素跟着动"。行组与行的 LayoutBox 也补上 `Position`：`relative` 的表部分要按定位框的次序绘制，否则虽然几何对了、绿色仍被普通内容里的红色盖住。百分比按 CSS 2.1 §10.5 只在表格声明了高度时才有整份可取，未声明则不生效。全量：wpt 4134 → 4150，16 对翻绿、失败集 0 回归；单测 1854 → 1856（`ARowGroupStandsOffThePlaceItsRowsWereLaidIn`、`APartOffsetSaysNothingUntilTheTableStatesAHeight`）。
  - **这一轮我自己踩到的两个坑（备查）**：(a) 第一版没加"百分比需表格声明高度"这条门禁，`position-relative-008/009` 立刻从通过变失败 —— 规范页的 assert 文案就写着 `top: 100% doesn't resolve if the parent doesn't have a height specified`，读题不够细；(b) 新加的第一条单测最初拿 `head.y - body.y` 当判据，实测得 88 而不是 100 —— 因为两组的排布本身随表格高度变化，正确的尺子是"同一页写两遍（有偏移/无偏移）求差"。先测量再写断言又一次救了猜数。

- [ ] **并发下工厂缓存被写坏（引擎缺陷，非语料问题）**：两次全量各报 1 个"画不出来"的页面，页面名不同但栈相同——`InvalidOperationException: Operations that change non-concurrent collections must have exclusive access`，栈顶 `WideKeywordDeclarationFactory.Create`（`src/Hutu/Rendering/WideKeywordDeclarationFactory.cs:36` 的 `Dictionary _cache`）← `MatchedCascade.Scan` ← `CssStyleDeclaration.TryGetProperty` ← `BrowsingContextExtensions.GetDeclarationInfo`。被写坏的是我们那个 `_cache`，说明**一个工厂实例被两个线程共用**；而 `HutuViewInternal.CreateBrowsingContext` 看起来是每个视图 `new` 一个并 `.With((Object)factory)` 注册的（`src/Hutu/HutuViewInternal.cs:685`），所以要先取证是哪一层把实例共享了出去（`Configuration.Default` 是静态的、里面全是**实例**而非 creator，`With()` 只做 `Services.Concat`；可疑点是 AngleSharp 里是否还有按类型缓存服务实例的表）。两条验证路径：给 `_cache` 换成 `ConcurrentDictionary`（快、掩盖真因），或查出共享点改注册方式（对）。
- [ ] **block-in-inline 拆分（CSS 2.1 §9.2.1.1）**：一次拿下 `CSS2/visuren` 的 6 对（`emptyspan-1..4`、`split-inner-inline-1/2`、`whitespace-present-1a`）以及 `percent-height-1`，签名全部同源。已用 deep-look 取证：`emptyspan-1` 我们现在把带边框的外层 inline 画成一个框（`border 8,8 784x27.72`）并把内层 block 塞进去；Chrome/规范是把 inline 拆成块前段与块后段两个匿名 inline（参考页正是 `block` 在前、空 `span` 在后）。引擎目前没有匿名 inline 机制（`src/Hutu/Rendering/Layout/InlineLayout.cs` 只做行内测量，拆分要动盒树形），属架构级改动。
- [ ] **`calc-size()`（CSS Values 5）**：整目录 `wpt/css-values/calc-size` 16 对里 14 对失败，其中 `min-max-sizes-001..006` 六对签名完全一致（6000 像素、首个差异同在 48,50）。仓库内零实现（`rg calc-size src` 无命中）。落点已查明：`StyleLength` 需新增 kind（可复用 `StyleMathFunction.Sum` 那套"保留各项单位、布局期才求值"的机制），求值口在 `LayoutEngine.ContentSizedWidth`（`src/Hutu/Rendering/Layout/LayoutEngine.cs:1586`，`fit-content` 已在此实现）。关键语义：`calc-size(auto, size + 80px)` 要对 min-content 与 max-content **各自**求表达式（100→180 与 40→120），再走 `fit-content` 夹成 100；且 intrinsic 要继续向外传播。
- [ ] **竖排表格轴置换**：`gecko/writing-mode/tables` 16 对全灭，其中 14 对同一根因（vlr/vrl 成对且像素数完全一致）。`fixed-table-layout-025-vlr` 已取证：表格 100px 高、三格各 `padding:25px 50px`、中格 `height:50%`，Chrome 得 100×100；我们画成 `table 300x100` 且三格横向排在 x=8/108/208 —— 即 vertical-lr 下仍按横排表的"行内=水平"推进。§17.5.2.1 的列宽分配在竖排里要换成块轴。
- [ ] `wpt/css-gaps/grid` 剩 58（含 fragmentation 28）、`css-gaps/flex` 44：gap 装饰族。已收的部分见 §8；残余多是 ±0.5px 子像素取整交替（deep-look 量过 `flex-gap-decorations-040`：band 中心我们 75.75/158.25/240.75/323.25，参考 76.25/157.75/…，单根因收益低），以及 `justify-content` 分布空间计入 gap 的尺寸（CSS Gaps 1 §2.1"Such additional space increases the size of the corresponding gaps"）—— flex 侧已按实际摆放算 gap，残余要先确认是谁在取整。
- [ ] （较后）margin-collapsing 负边距 self-collapse 与测量感知 min/max-height 族。
- [ ] **`text-decoration-skip-spaces` 整条属性未实现**（`rg -i skip-spaces src deps/AngleSharp.Css` 只命中无关的 `SkipSpacesAndComments`），语料里对应 `wpt/css-text-decor/text-decoration-skip-spaces-001/002/003/004/006`，5 对签名同为"行首行尾的空白该不画线"。规范 §2.10.3：初值 `start end`，`none | all | [start || end]`，继承。**但是**：先别指望实现它就能翻绿——实测 diff 图（`build/reftest-diff/text-decoration-skip-spaces-002.png`）显示行 81..83 只有约 12 像素是"下划线端点"（正是本属性该管的），而行 57..77 有约 1400 像素落在**字形本身**上：我们画 `#ffffff`、参考画 `#ffa500`，稳定在 x=96..101 / 139..144 / 162..167 / 182..187 这些列上。两页的盒几何其实一致（实测：两边 `ABCDEF` 都落在 x=72..198，页宽/位置都相同），差别在于我们把"8 空格 + 6 字母 + 8 空格"当**一个 blob** 排版、参考页把字母拆进 `<span>` 单独成 blob，于是 HarfBuzz 的字形落位差半个像素。也就是说这 5 对被**同一 blob 内的字距/落位**卡住，skip-spaces 只是其中一小条。要打这族，先量"字母行"那 1400 像素的来源（blob 起点与 advance 累计），否则做完属性仍旧全红。
- [ ] **多栏容器"分栏行 + spanner"没有网格算术**（`wpt/css-gaps/multicol/multicol-gap-decorations-002/008/009/010/011/012`，6 对；签名 `72600 像素 / 首差 62,2`）。已用探针逐层核到根因，不是猜：
  - 样式读取**完全正确**：`column-height:60 + column-gap:10 + row-gap:10 + column-wrap:wrap + column-width:60 + column-count:3 + column-fill:auto` 在 196 宽的容器上，实测 `IsMultiColumn=True`、`Used(196)=(3,60)`、`Grid(196,196)=(3列,60宽,60高,2行)`。
  - 布局**没走分栏**：把 6 个 `<p>` 画成 6 个 200 宽、纵向堆叠的块（子盒 y=2,62,122,140,200,260）。原因在 `BlockLayout.ColumnsOf`（`src/Hutu/Rendering/Layout/BlockLayout.cs:871`）有一条**有意的回退**：`child.Style.SpansColumns && box.Style.Multi.Wraps` 直接 `return false`，注释写着"a wrapping container would have to put the spanner in a row of column boxes this does not do"。去掉 `<h2>` 后同一页立刻正确分栏（实测子盒变成 (2,2)(72,2)(142,2)(2,72)(72,72)(142,72)，3 列 60 宽、两行）。
  - 缺的不是入口而是算术：分节路径 `MeasureMulticolSections`/`ArrangeMulticolSections`（:1935/:2920）里每节只按"一横排列"算 extent、`extents` 长度取 `children.Count`（fills）或 `columns`，没有普通路径那段"列盒 = column-height、行数 = ⌊(容器高+row-gap)/(列盒高+row-gap)⌋、超出往下一行"的逻辑（普通路径 :1034-1052 量度、:2742-2772 排布、:2779-2783 规则长度）。`ArrangeMulticolRun` 本身已支持行列（`cell % columns` / `cell / columns`，:2822-2827），所以补的是**每节的 extents/rows 计算 + spanner 在行间的落位**，不是重写。
  - **代价提示**：参考页是作者手画的近似（三段绝对定位 flex + 逐个 `.column-gap`/`.row-gap`，注释还写"Remove the 6px consumed after the spanner in the first row"），要 0 像素差需要精确对上 Chrome 的 spanner 前后列盒消费顺序。6 对同簇，但一次改动要吃满 6 对的可能不大——先做能过 2~3 对的中间态也算进展。**这条先别和上面 skip-spaces 那条混做。**
- [ ] 两个 `[Explicit]` 让开的单测：`:focus`（AngleSharp `HtmlElement.DoFocus` 空实现）与孤立代理项转义（AngleSharp 解析阶段丢弃声明）。等依赖能满足时摘掉。

---

## 8. 2026-10-27 一轮：gap 端点、计数器、绘制趟、`:link`、固定表宽

基线（同一天，全量，`HUTU_SCORE_LISTED=9999`）：**wpt 4129 / 5834、gecko 1697 / 2471**；findings 由 2482 降到 **2479**。单测 **1852 passed / 0 failed**（另有 2 个 `[Explicit]`），硬断言 **55 passed / 0 failed**。

| commit | 做了什么 | 依据与取证 |
|---|---|---|
| `fa24e308` | gap 装饰：悬空端点按 cap 处理，不再 overlap-join | CSS Gaps 1 §3.3；Chrome `IsCapIntersection`/`HasCrossGapSegment`（fork 里读到的原文）。wpt +11、0 回归 |
| `cf9109cd` | 实现 `ethiopic-numeric`；并修正 4 条过期断言 | §7.2 算法 + 语料 38 值逐字节比对（直接解析页面实体，不手抄）。`cjk-earthly-branch` 13+ → `一三、` 由 WPT `css3-counter-styles-202` 的 assert 与 ref 双重确认 |
| `0e75a56f` | flex/grid item 回到 inline 遍绘制；定位的 inline 进它自己的层 | Flexbox 1 §4.3"paint exactly the same as inline blocks"+ Grid 1 §9；第二项修掉 `contain-layout-018`/`contain-paint-025` 的**假通过**（HEAD 上 `FAIL` 文字压根没进绘制列表） |
| `8b11975b` | `:link` 承认 `href=""` | Selectors 4 §8.2；AngleSharp `IsLink()` 要求 href 非空，故在 Hutu 侧注册 `IPseudoClassSelectorFactory`（放在 `WithCss()` 之前，令 fork 的强制状态包装包在我们之上）。顺带按"只收静态布局"的政策移除 1 对靠 `onload` 改 DOM 的语料，并给导入器补上 `\son[a-z]+=` 规则 |
| `b25c2cd0` | 更新 `mixed-flow` golden | 段落边距 `17.92→16`（Chrome `html.css` 用 `1__qem`，有 doctype 即 `1em`）＋ item 进 ink 遍导致的绘制序，逐行核过 |
| `65b6b3d7` | 2 个 AngleSharp 依赖的红改 `[Explicit]` | 见 §7 末条 |
| `33e746f6` + `a6dbc9f9` | 固定布局表格听声明宽度 | §17.5.2.1 vs §17.5.2.2：`Math.Max(width, minContent)` 那个下限属自动算法。实测 fixed→25、auto→42；+4 对（`contain-paint-cell-001/002`、`contain-paint-table-001`、`flexbox_table-fixed-layout`）|

**这一轮学到的两条流程教训**
1. **别用 `git checkout <sha>` 做验证**：会进入游离 HEAD，之后的 commit 就落在分支之外。本轮确实把 `main` 弄丢过 2 个提交，靠确认父链后 `git merge --ff-only` 修回。验证基线请开 worktree，或结束前一定 `git checkout <branch>`。
2. **bisect 谓词必须只针对目标症状**：第一次写成"golden 全绿"，而区间内另一条 golden 也红着，于是每个提交都判 bad、归因错到无关的 multicol 提交。改成只匹配 `mixed-flow` 后才拿到正确答案。
3. 顺带：`HUTU_CONFORMANCE_UPDATE=1` 会重写 golden 且把该次运行报成 skipped——只为读 diff 而跑它，也会真的改动 baseline 文件，跑完必须核对 `git status`。

## 9. 2026-10-27 后半轮：spanning cell、表部分相对偏移、单元格百分比、竖排强调号

基线（当天全量，`HUTU_SCORE_LISTED=9999`，取整轮起点）：**wpt 4115 / 5834、gecko 1694 / 2471**；本轮结束时 **wpt 4157、gecko 1697**，失败清单 2482 → **2451**。单测 **1857 passed / 0 failed**（另有 2 个 `[Explicit]`），硬断言 **55 passed / 0 failed / 1 skipped**。

| commit | 做了什么 | 依据与取证 |
|---|---|---|
| `0a55ff65` | 跨行列的格子按自己那根轴的室宽来撑 | §17.5 与 `colspan` 在竖排下的轴置换；先量后写，改动只作用于跨格 |
| `7df557e8` | 表部分（组/行）写 `position: relative` 时真的平移自己 | CSS Positioned 3 §6.2 把表部分按块框处理；`-absolute-child` 那 8 页要的是"行内绝对子元素跟着走"，故用 `MoveWithContent`；并给 LayoutBox 补 `Position`，否则几何对、绿色仍被红色盖住。16 对翻绿 |
| `7a63e476` | 表部分的百分比偏移按**声明高**求解 | CSS 2.1 §10.5 + 页面自己的 assert `resolves against the specified parent height (not the used height)`。上一版读 used 高，让两页"因错而通过"；改后 011/012 只差一条 10 高的带子 |
| `fa84e87d` | 中文读数里"十"的"一"何时省 | §7.1.1.3 字面会把 10011 也省掉，gecko 参考页写 `一万零一十一`；两簇语料的合取证据是"个位为空或本组为首组才省"。+3 |
| `5b04a64b` | 中文的省略规则不再顺带决定日文/韩文 | 规范按语言给三条；`UnderTenThousand` 原为共用一条判据。语料暂覆盖不到日韩这类值，先把策略字段留出来 |
| `a98bc0ef` | 竖排强调号的预留带改按**符号站的哪一边**扣 | CSS Text Decoration 4 §5.2 + Writing Modes 3 §5.1：两个竖排模式转向相同（都顺时针 90°，只有 `sideways-lr` 模式逆转），所以"向右够出"恒为 ascent；旧代码读 block-end，在 `vertical-lr` 下 block-end 恰好吃掉右边缘，基线落进带内（base 41.91 而非 33.91）。css-text-decor 111 → 119、wpt 4149 → 4157 |

**这一轮的流程教训（新增两条）**
1. **写文档要用 .NET 显式 UTF-8 API**：PowerShell 的 `Get-Content`/`Set-Content` 默认按 ANSI(GBK) 解码，把整篇中文文档读成乱码再写回去，一次就毁掉 165 行。规程：`[System.IO.File]::ReadAllLines($path, (New-Object System.Text.UTF8Encoding($false)))` 读、`WriteAllLines` 写，脚本本身保持纯 ASCII；改完立刻 `git diff --stat` 看行数是否与预期一致（这次是 165/163，一眼可见）。恢复只用 `git checkout -- <file>`，且要先确认该文件在本次损坏之外没有别的工作树改动。
2. **一次改动只留一份快照**：把每次全量结果复制成 `build/score-after-<特征>.md`，比对脚本只取"失败行"（`- \`path\` - ` 带说明的那种），不要取通过清单——第一版脚本把通过项也收进集合，得出"0 新增 0 修复"的假象。

## 10. 2026-10-28 凌晨：强调号的走位、表格的强制断点、符号样式的词序

起点是 §9 结束时的 **wpt 4157 / 5834、gecko 1694 / 2471**（失败清单 2451）；现在 **wpt 4188、gecko 1704**，失败清单 **2413**。单测 **1867 passed / 0 failed**（另 2 个 `[Explicit]`），硬断言 **55 passed / 0 failed / 1 skipped**。

| commit | 做了什么 | 依据与取证 |
|---|---|---|
| `84044961` | 不被加符号的字符在走位里照付自己的宽度 | CSS Text Decoration 4 §5.4：`skip` 只是"这个字符不画号"，不是"这个字符不存在"。旧的循环从"上一个被标记的字符"跳到"下一个"，只累加了被标记者的 advance，于是括号、句号、空格全被抹掉宽度，它们后面的号整串往回偏。先量后写：`Abc (def)` 里第二个号本应落在 `d`（x=41.42 那一档），旧代码把它落在空格上。已有的中文那句单测只核"有几个号"，所以它一直是绿的、却没看见位置错了——现在补上中心点 `2.5, 12.5, 22.5`。punctuation-1/2 两对翻绿，css-text-decor 119 → 121 |
| `aaf85ea9` | 表格内部的强制断点推到表格边界 | CSS Break 3 §3.1/§6 + 页面自己的注释 `Should be propagated to the outer table`。栏不能在表格零件之间结束，故 cell（或 cell 内子盒）上的 `break-after: column` 归到表格的后面；`avoid` 与强制的冲突由现成的 `Fragmentainer.Combine` 裁决。表内的 `avoid` 不外传；裸 wrapper 不走这条路（切片器早已把它拆开）。4 对同签名翻绿，`css-break` 模块 86 → 90 |
| `594457e2` | 钉住上面那条的单测 | 同一页写两遍（差别只有 cell 上那个断点），差值就是规则本身 |
| `be6c1a29` | `text-emphasis-style` 的 filled/open 按整个值读，不看词序 | §5.1 的 `[ [ filled | open ] || <shape> ]` 两种顺序都合法；旧读法一遇到形状词就立刻按"当前默认填充"取字形，`circle open` 于是取了填充圆，后面那句 `open` 无处可改。**带引号的 token 不参与这一轮分类**（语料里有 `text-emphasis-style: 'V'`，若在去引号之后再判词，会被当成默认填充、丢掉符号本身）。5 对翻绿，css-text-decor 121 → 126、wpt 4164 → 4169，失败集 0 回退；单测 1859 |
| `7f1a51e7` | 格子的内容对齐按"流内占位"量剩余空间，而不是按"绘制位置" | CSS 2.1 §9.4.3：相对偏移只挪绘制、不挪占位。旧代码用子盒当前 `BorderBox` 量 free，于是把偏移当成"内容不够高"的证据先移一半（(100−90)/2=5），通用路径再按声明移 10 → 净 −5。新增 `LayoutBox.RelativeDisplacement`，在**两个**平移入口都记录（`LayoutEngine.Arrange`、`PositionedLayout.Translate`——后者是 inline 分片的移动口），对齐时减回。单独把 011/012 带绿（css-position 35→37）。单测把同一格写三遍（无偏移/上 10/下 10）取差，回退后红在 ±5 |
| `28d9e04e` | 单元格的百分比偏移按**行所声明的高**求解 | CSS 2.1 §10.5，010 要 0、013 要 10，旧行为两种都移 100（行的 used 高，被格内 100px div 撑出来的）。走"表格侧自算位移"那条（§7 的候选 b），`LayoutEngine.Arrange` 增可选 `Offset? displacement`，表格传 `PartOffset(cell.Box, grid.Width, StatedHeight(row.Box, 0))`，通用路径**取用不相加**，因此只平移一次。没有把声明高塞进格子 CB（那会连带改格内子元素百分比高度的 `definite` 判定）。css-position 37→39、wpt 4169→4173，`css-tables`(84) 与 `CSS2/tables`(7) 作闸门读数不动。单测同一页写三遍（行不声明/10/40）取差，回退后红在 100 |
| `47e076db` | 装饰线从它所站的那条边**朝背向文字的一侧**生长；line-through 居中 | CSS Text Decoration 4 §2.4/§2.5。旧代码对所有线型都从位置线向正方向画带，于是加粗一条线遮住的是"位置线与文字之间"的空，而不是位置线之外——借来的页面正是拿"把线加粗到超过整个盒子、要求盒子被盖满"来考的。竖排下"朝外"就是偏移本身被计入的那个符号，所以判据取轴向符号而不是"线落在基线哪一侧"（第一版这样写，把 `text-underline-offset` 为负的下划线翻成了压在文字上的带，一页 gecko skip-ink 因此两图画成一张）。实测（Segoe UI 40px）：underline 4px→行 60..63、24px→60..83（头不动）；overline 4px→10..12、24px→0..12（尾不动）；line-through 4px→44..47、24px→34..57（两头各外扩 10，对称）。+2 对（thickness-linethrough-001、-overline-001），另 thickness-vertical-001 从 1600 像素降到 **20**（只剩末行 1 条抗锯齿边，子像素问题，已记档未打）。3 条单测读真实墨迹的上下缘，并把"细线自己的边"当尺子；在无修复版本上验红（line-through 两头都不长、overline 朝文字方向长） |
| `5a2bc1d8` | 装饰长度里的 `calc()` 按该属性自己的单位折叠 | §2.4/§2.8：这两个性质的百分号是"文字所设尺寸的份额"。旧读法只给 min/max/clamp 走这个基准，`calc(50% + 4px)` 落到通用长度读法——百分比按**绘制宽度**、`em` 按**设备字号**——于是 8px 文字上的 8px 线变成 404px 的杠。补一条 `EvaluateSum` 通道复用同一个 `DecorationTerm`。单测把同一条线写三遍（定值/百分号/`em`）要求画出同一条带；无修复时百分号得 200px 杠、`em` 短 12px。+1 对（thickness-calc），另 text-underline-offset-calc 从 1684 降到 **80** 像素（已是抗锯齿级） |
| `ccf88bb8` | `flex-wrap: wrap-reverse` 把行搬到箱子另一端，但色表仍按流内行序发 | CSS Gaps 1 §4.6。取证靠参考页自己把缝画在哪一行：`flex-gap-decorations-multi-value-wrap-reverse-uneven-lines-ref.html` 用 6 个绝对定位 `.col-gap` 把 (top,color) 写死，对照实测（探针读 `BorderCommand` 的 y 与颜色）——流内第几行不变、它在页面上的位置要跟着翻。旧代码两半都错在"用行的逻辑交叉位置"：行缝中心与 item 缝的跨轴范围都从 `CrossStart` 起算，`wrap-reverse` 下排布把它们挪到了另一端，缝却没挪。css-gaps 163 → **166**、wpt 4176 → 4179；同族另 3 页从 6400/7140/2660 像素降到 5300/6140/2460（更接近、仍红，剩的是分片容器如何共享行） |
| `619e8017` | 半个像素的边一律**取更远的那个像素**，与本文件里填充的做法统一 | 同一个 `SkiaPaintExecutor` 里两套答案：`Whole()`（裁剪路径/多边形）用 `AwayFromZero`，而 `Snap`/`SnapEdge`（边框一边、gap 装饰条、滚动裁剪矩形）用默认 `ToEven`。取较近偏下时，一条落在奇偶错位缝中间的 5px 装饰条两头各掉 1px、变成 4px 宽。语料是硬证据：`css-gaps/flex` 040~046 与 `grid/081` 的参考页用**作者手写的分数偏移**画规则，期望的是"取更远"；`gecko/pixel-rounding/*-5` 那族（边线站在半像素上）同向受益。wpt 4179 → **4188**、gecko 1697 → **1704**（css-gaps 166 → 175、pixel-rounding 34 → 40），`css-multicol`(105) 与 `css-borders`(37) 作闸门读数不动，失败集 **17 修复 / 0 真回退** |
| `16975378` | 只有**内联轴**的装饰能让一条线"不是 phantom"，且按边各算 | CSS Inline 3 §2.3（w3c/csswg-drafts#9344 把这条改成"只算 inline axis"）。旧判据 `LeavesALine` 问了两件事：横竖**两个轴**都算（于是只有 `padding-top/bottom` 的 span 也让线存在）、而且用 `Horizontal = Left + Right` 这种**求和**（于是 `margin-left:1; margin-right:-1` 相加为 0 就"不算"）。改法：`flow.InlineMargins(...)` 取 Start/End，两边分别判非零，块轴整个退出判断；文本/换行/替换/原子 inline/自有内容这些"线存在的理由"不动。+6 对（`css-inline/model` 6/6 清零），css-inline 55 → 61、wpt 4188 → 4193、gecko 1704 → 1705。闸门按子模块逐个核过基线：`CSS2/normal-flow` 21/31、`CSS2/visuren` 1/10、`CSS2/floats-clear` 6/23、`css-box/margin-trim` 0/7、`css-text/white-space` 232/255、`gecko/margin-collapsing` 260/263 —— 全部与基线**逐字相同**（最后一个是"存在性判据错了最先炸"的族）。单测 `APhantomLineAnswersToTheInlineAxisEdgeByEdge` 量同一种边距塌陷在五种写法下的落点（16=塌陷穿透、24=线存在），在无修复版本上两处答案正好互换。 |

**这条验证手法记一下（有用，且有坑）**：要证明一条新单测真的钉住了修复，最好的办法是在**修复的父提交**上跑它、看它变红。这次先试 `create_worktree`（ref 指到父提交）——失败：worktree 里 `deps/AngleSharp` 子模块没初始化、且 vendored fork 的引用路径不同，整个依赖图编译不出来。可行的替代是在主仓里 `git checkout <parent> -- <那一个引擎文件>`，只跑这一条测试，看完红之后 `git checkout HEAD -- <同一文件>` 还原，并用 `Get-FileHash` 比对还原后的文件与动手前的备份（这次 `identical: True`）。红线要落在症状本身（after 留在栏首而不是进下一栏），不是编译错误或无关的失败。

**这一轮的取证教训（三条，都是我自己踩的）**
1. **别在同一批里跑两个 `dotnet`**：两个 test/build 进程共用同一棵 `obj/bin`，彼此的输出会互相覆盖。我就这样拿到过一次"基线读数"——同一条探针在串行的两次运行里给出 4 列和 5 列，先前那次 5 列其实是另一个进程留下的 DLL 的结果。规矩：量基线必须独占一次构建，且核对自己的改动确实被换出去了（`rg` 一眼看源码里那个符号在不在）。
2. **`git stash` 与 `File.Copy` 都保留旧 mtime**：还原源码后 MSBuild 认为"不必重编"，于是 `--no-build` 跑的是上一个版本的 DLL，读数完全相反。还原之后要 touch 源文件（`(Get-Item x).LastWriteTime = Get-Date`）或直接 `dotnet build`，并用 `Get-FileHash` 确认内容与动手前一致。
3. **写断言要挑真正能区分的通道**：我给"金色装饰条"写的第一版是 `Red > 128`，而白底 `#ffffff` 的 Red 也是 255——这条断言在有无修复时都通过，等于没钉住任何东西。金色是 `#ffd700`，该看的是 Blue。凡是"红/绿/白"三态的像素断言，先问一句：把待测物换成空白，这条断言还成立吗？

## 11. 2026-10-28 上午：内联级绝对定位的静态位置，以及两族待做的侦察结论

起点是 §10 之后的 **wpt 4200 / gecko 1705**（`901f84a3` 的读数）。本轮只落了一刀：

| commit | 做了什么 | 依据与取证 |
|---|---|---|
| `2612678b` | 指定 display 为内联级的绝对定位盒挂在**线盒插入点**上，不再站在容器起始角 | CSS Position 3 §3.1 的静态位置 + Chrome 实测（2026-10-28，100x60 相对容器、20x10 子盒）：`text-align` 左/中/右把**空行**的插入点放在 0/50/100（注意中线是 50 而不是"20 宽盒居中"的 40——绝对定位盒不给行加宽）；`direction: rtl` 的裸容器挂在 80（右缘起点、盒向左伸）；`float:left 30` 先缩短行，插入点 30；rtl + float:left 得 80、rtl + float:right 得 50；`display:block` 的绝对盒在 center 下仍是 0。词法上要看**页面写的 display**（AngleSharp 计算值仍是 inline，引擎在 `BoxBuilder` 里自行 blockify），`inline-block`/`inline-flex` 同属内联级。落点两处：块路径 `BlockLayout.StaticLineOf`（新增 `StaticLine` 结构 + `PositionedLayout.Horizontal` 的 `Anchor`），行内路径 `InlineLayout.Place`（并给 `Held` 带上同一个锚，`position-absolute-in-inline` 那族走的就是 Hold）。竖排的插入点沿 y 走，本刀只回答横排（守卫在两条入口）。
| 读数 | wpt 4200 → **4219**、gecko 1705 → **1706**（+21/−0） | 九页 `inline-level-absolute-in-block-level-context-001..012` 全收，另带 `gecko/bugs/363637-1`、`first-line/469227-2`、`CSS2/positioning/auto-position-rtl-child-viewport-scrollbar`、`grid-gutters-014`、`preserve3d-button`、五页 css-variables、两页 `nth-child-of-*`。唯一"丢"的是已知 flaky `gecko/bugs/370525-rowspan-1b`，它这次是**画不出**（§7 的并发工厂缓存异常），不是图差。单测 **1870 全绿**；新测试在父提交引擎上红在六个实测位置（100/50/0/0/0/50）。 |

**两个子 agent 的侦察结论（只读、未落地，留给后续）**

- **ruby-overhang 族（18 对）**：`ruby-overhang` **完全没读**，AngleSharp 只认 `auto|start|end|none`（`deps/AngleSharp.Css/.../Map.cs:829`，`spaces` 直接丢），引擎侧零命中。基准宽度在 `InlineLayout.cs:2447`（另有 intrinsic 孪生 674-676）取 `max(基准, 标注)`；标注高度被**加进行升部**（5634-5643、2450/2459）——这正是 `ruby-overhang-none` 基准字低 8px 的全部原因（CSS Ruby 1 §3.6：标注不影响行高）。最小改法：①注册 `spaces` 并按"标注宽 − 两侧可溢出空白"缩短基准；②去掉 `+= annotation.Block`，标注画在行盒上沿之外（1755/1764/1788 同步）。`ruby-align` 同样未读（居中硬编码）。
- **css-text 的 98 对按根因分簇**：`text-autospace` **10 对**（整条属性未注册，Chrome：表意/非表意边界各 1/8 em，参考页用 `margin:5px@40px` 伪造）、`text-fit` 10 对（未实现，逐行缩字号）、南亚脚本断行 8 对（Thai/Khmer/Lao/Myanmar/Javanese 无断点，`LineBreaks.cs:339` 全部落到 AL）、`white-space: break-spaces`+U+3000 9 对、`overflow-wrap: anywhere/break-word` 8 对；`white-space` 总数 23 但分属六种机制，不能当一条打。
- **`float-in-rtl-slr`（10 对）**：Chrome 已测明 `sideways-lr` 下 `float:left/right` 的"线左/线右"要相对 vertical-rl/lr **翻转**（`FloatsAtTheInlineStart` 少一个 `TurnsCounterClockwise` 因子；`FloatSideOf` 要同步改成由它反推），但整族**还要**"列里的浮动侵入后续兄弟的行盒"——`FloatBand`/`LineRoom` 只按 x 轴写死，列路径用局部的 `ColumnFloats`，`TracksFor` 的注释也明说列被有意排除。两者同做才有 10 对的净收益，属一刀大改。
- **gap 装饰的 fragmentation 家族（flex 30 + grid 28 + subgrid 18 + multicol 16 = 92 对）**：同源于"分片容器里的 flex/grid 不被切片"。`flex-gap-decorations-fragmentation-003` 的探针显示 flex 容器整块 174x114 停在 30 高的列里，参考页按 4 列手画规则；这是 §7 里"多栏 + spanner 无网格算术"同级的架构缺口。

## 12. 2026-10-28 下午轮：浮动的"上下文"、marker 钉在哪条边、以及一串不换行空格

起点是当天上午的 **wpt 4219 / 5834、gecko 1706 / 2471**（失败清单 2380，含 1 对"画不出来"）；本轮结束时 **wpt 4221、gecko 1734**，失败清单 **2350**，**修复 30 对、回退 0 对**。单测 **1870 → 1877**，硬断言 **55 passed / 0 failed / 1 skipped**。

| commit | 做了什么 | 依据与取证 |
|---|---|---|
| `672c69b6` | 容器持有自己的浮动时，**同时**读上下文里的浮动；浮动只被"对面那个真会压到的浮动"往下推；RTL 按**结束边**量；outside marker 改成"钉 border 边 + 按浮动的侵入量平移"，且盒子自己持有的浮动不推它自己的 marker；`.NET` 把 U+00A0 当空白导致整段文字被丢；浮动自己是 BFC 根，摆放时把 band 密封 | 全部先量 Chrome 再动代码，见下 |

**这一族的形状**：`gecko/bugs/428810-*` 33 对里 26 对红，每对都是**正好 36 像素**＝一个 6×6 的圆点。动手前它是全库最大的"近失同质簇"，也是 §11 两份备忘录都没点到的地方（`gecko/bugs` 那份按 N 相同聚类命中了它，但把它记成"一个函数"——实际是四处独立缺陷叠在一起）。

**取证工具（新增三件，都在 `build/` 下，gitignored）**

| 工具 | 回答什么问题 |
|---|---|
| `build/_ink` | 真实 Chrome 把 test / ref 各画一遍，打印"非白像素包围盒 + 像素数 + 两图差异数"——一对页面里只有一个圆点时，它就是"浏览器把 marker 放在哪儿"的数字 |
| `build/_query` | 在 Chrome 里跑一段 JS，把 `getBoundingClientRect` / `Range.getClientRects` 的真实几何读出来（内浮动到底落在 y=10 还是 y=20，行又在哪） |
| `build/_engineink` | 同一个问题问我们自己的引擎，输出与 `_ink` 同格式，可逐行对读 |

先量后写的一条硬结论：**marker 的落点不是"行的起点"**。Chrome 在 14 个合成页上拟合出来的公式是
`marker 外缘 = border 边 + max(0, 浮动边 − content 边)`，并且**盒子自己持有的浮动不参与**（`m-inner15-text` 里文字让到浮动右侧，圆点仍停在盒子起始边）——旧代码写成 `max(border, 浮动边)`，差的就是 border/padding 那一段，正是 `-insets` 那 7 对。

**四处根因，逐条记一下（下次别重复侦察）**

1. **上下文丢了**：`FloatBand.cs` 顶部注释本来就写着"容器一旦持有浮动，就只把自家浮动传给子节点"。CSS 2.1 §9.5 要的是同一个 BFC 里所有浮动排成同一趟：新浮动要躲开上下文的浮动（内浮动 Chrome 放 y=20，我们放 y=10 压在外浮图上），后面的行也要同时被两批缩短。改法是给 `FloatTracks` 加 `_aroundLeft/_aroundRight` + `Hold()`，查询全读两批、`Absorb` 只上交自己持有的——否则祖先的浮动会在每一层被重复平移一次。`FloatTrack` 顺带记下 `Owner`，marker 用它排除"盒子里自己的浮动"（按元素祖先链判）。
2. **"放得下"的参照物**：`Place` 原来判 `x + width <= right`，而 `right` 里混进了**容器自己的边**。§9.5.1 把浮动钉在包含块边上、只要求它别压到别的浮动，所以比容器还宽的浮动应当溢出而不是下潜——`other-float-outside-rule-3-left/-right` 就是这一条（Chrome：425 宽留在 y=0，不潜到 y=300）。新增 `FarEdgeAt` 只看对面的浮动。
3. **RTL 的边**：band 只带 `Left`，而 RTL 盒子的左边要等宽度定完才知道（§10.3.3 里宽度正是待解量）；结束边却可以直接算：父盒结束边 − 子 margin-right − border − padding。给 `FloatBand` 加 `End`，`Room` 的 reversed 分支与 `FormattingRoot` 都改读它，`StandBeside` 的帧平移也按 End 走。这一步单独修好了 15 个 RTL 对里的绝大多数。
4. **`.NET` 的空白≠CSS 的空白**：`BoxBuilder.GroupChildren` 用 `String.IsNullOrWhiteSpace` 判断"这段文字是否可见"，而 .NET 把 U+00A0、U+3000 等都算空白，CSS Text 3 §4 明确它们不可折叠、不可裁、要测量。于是"块级浮动兄弟 + 5 个 nbsp"的整段内容被当空白丢掉，行盒根本不存在（2a/3a 那 8 对就卡在这）。换成 `TextChunker.IsCollapsible`。

**顺带**：`428810` 修好后同一提交连带收掉 `413840-ltr-offsets`、`CSS2/floats/overhanging-float-paint-order`、`css-flexbox/css-box-justify-content`；`370525-rowspan-1b`（§7 记的并发工厂缓存那条"画不出来"）本轮两次全量都没再复现，先记为偶发，不当已修。

**这一轮的三个自己的坑（都值一条记录）**

1. **`--no-build` 会替你保守一个谎言**：中途一次 `apply_patch` 让编译失败，但我仍用 `--no-build` 跑了记分——跑的是**上一个 DLL**，于是把一次真实的"回退 0"读成"回退 3"，又反过来把一次修复读成没修。规矩：每次改完引擎，先看 `dotnet build` 成功，再记分；归因实验也一样。
2. **读子集报告不能按行号 skip**：`reftest-score.md` 是"每个语料一段"，我用 `Select-Object -Skip 2 -First 2` 取"wpt 段"，实际取到的是"gecko: 0 of 0"那一段，于是追了一整轮**根本不存在的回归**（`grid-margins-no-collapse-002`）。要么按 `^## ` 分段取，要么直接 grep 页名。
3. **备份点要晚于本轮所有已落地的修改**：我做属性归因时把 5 个引擎文件复制到 `.tmp/round-backup`，之后又补了一处（浮动密封），恢复备份时它被悄悄吃掉——是新写的单测 `AFloatingBoxTakesNoRoomOutOfItsOwnLines` 把它抓回来的。备份/回退做完一定要 `git diff` 对一遍，或干脆用 `git stash` + 立即恢复。

**下一步的候选（按"对数 ÷ 工作量"排，来自本轮 4 个只读 Subagent 的备忘录，全部未落地）**

- **marker 家族连坐**：`wpt/css-lists/list-and-{flex,grid}-001`、`css-pseudo/marker-text-decoration-skip-ink`、`gecko/list-item/bullet-space-1`、`179596-1a/1b`、`507187-1` 都是同一个圆点的签名（36–38 像素）。`gecko/bugs` 备忘录把这族算 32 对，本轮收了 29。
- **一次一行的廉价项**：`:default` 只应命中表单默认按钮（4 对，改 `deps/AngleSharp/.../ElementExtensions.cs:360`）；`border-collapse: collapse` 时表格自己的 padding 不生效（4 对，`TableFormattingContext.Grid.cs:110`）；`rowspan="0"`＝到行组末尾（2 对，`:821`）；`text-combine-upright` 在 sideways 模式不该生效（2 对，三处构造点加一个 `IsSidewaysMode` 门）；`text-orientation: initial` 被当成继承（1 对，`ComputedStyle.cs:4547`）。
- **成块但边界清楚的**：`initial-letter` 竖排 raise/sink 只看 `box.Top` 不看 `Sink`（8 对，`FirstLetter.cs:298`）；ruby A2（标注不影响行高，CSS Ruby 1 §3.6）+ A1（`ruby-overhang: spaces` 没注册，AngleSharp 丢值）合计 31 对；表格 cell 基线对齐**根本没实现**（7 对，`TableFormattingContext.cs:525` 只有 middle/bottom）；根元素 `position:absolute` 与 canvas 背景传递（10 对，`LayoutEngine.cs:121`、`Painter.cs:156`）。
- **别当"一族"打的**：`css-anchor-position` 53 对里 30 对需要 `anchor()`/`anchor-size()`，是架构缺口；表格跨 fragmentainer（26）与 gap 装饰分片（92）同属"分片容器不切 flex/grid/表格"；`gecko/bugs` 227 对里 **80 对依赖 JS**（占该族 35%，按语料政策属"该退役"而不是"该修"）——这条需要用户单独拍板。
- **工具口径**：`build/_scorediff/{diffscore,nearmiss,analyze}.ps1` 分别做"失败集差集""近失清单（≤200 像素）""按目录聚类"。近失清单是本轮选目标的主要输入，值得每轮重生成。


## 13. 2026-10-28 傍晚轮：把"一条小规则"扫掉七条

起点 **wpt 4226 / gecko 1739**（失败集 2350）；本轮结束 **wpt 4231 / gecko 1741，失败集 2333**，两刀合计 **+28 修复 / 0 回退**。单测 1877 → **1885**，硬断言 55 全绿。（第一刀 `ab853ba7`，第二刀 `5718c0f1`。）

| 规则 | 依据 | 对数 |
|---|---|---:|
| `:default`＝表单里树序第一个提交控件；checkbox/radio 看 `checked`、option 看 `selected`，reset/普通按钮永不算，disabled 不影响 | Selectors 4 §8.1 + HTML §4.10.13；Chrome 探针两组共 20 个控件定案 | +6 |
| `border-collapse: collapse` 下**表格自己没有 padding**（cell 的仍生效） | CSS 2.1 §17.6.1；ref 页写死 `<div 100x100>` | +4 |
| 空表格的**声明宽**要算进 shrink-to-fit 的 preferred size；但**不是最小宽**（flex item 的 automatic minimum 读的是那对值） | §17.5.2.2 先读声明宽，CSS Tables 3 §4 + Flexbox 1 §4.5 说明声明宽不是 min-width | +4（并修掉 5 条自家回退） |
| `rowspan="0"`＝本行组余下的所有行（无组则到表尾） | HTML §4.9.11；ref 页直接写成 `rowspan="4"` / `rowspan="2"` | +2 |
| sideways 模式下 `text-combine-upright` 无效 | CSS Writing Modes 3 §5.1；ref 页直接删掉该属性 | +2 |
| `text-orientation: initial` 复位成 `mixed`（不是继承父级） | CSS Writing Modes 3 §5.1；**两条样式路径都要改**（ComputedStyle.DeclaredTextOrientation 与 ResolveTextOrientationFused） | +1 |

**教训（这次踩得很典型）**：同一条"读值"规则在引擎里往往有**两套解析器**——fused 快速路径与普通 ComputedStyle。只改一处时测试仍然全绿（探针页走另一条路），全量里也只收一半。改任何"关键字 → 语义"的映射，先 `rg` 同名解析函数，确认两条路径都覆盖。

另一个：把"声明宽"塞进 MeasureIntrinsic 的第一版同时抬高了 **min-content** 且解析了百分比，一次踩坏 5 条 css-flexbox/gecko 用例；全量差集立刻报出来，收窄成"只抬 max-content、跳过百分比与 ReadsContainingBlock"后归零。**内在尺寸的两个半（min/max）必须分别论证**，不能一起抬。
## 14. 2026-10-28 晚轮：ruby 不占行、emphasis 只落墨，以及一圈小规则

起点 **wpt 4231 / gecko 1741**（失败集 2333）；本轮结束 **wpt 4246 / gecko 1746，失败集 2313**：相对起点 **修复 20 对、回退 0**，相对上一份 2325 基线是 **12 修复 / 0 回退**。单测 1896 → **1897**，全绿。

| commit | 做了什么 | 依据与取法 |
|---|---|---|
| `34154a6a` | ruby 标注**不把行撑高**，只有超出 base 的 `line-height` 已经给出的 leading 时才加高；base 按自己的行高取 band，基线在 band 内测量；`ruby-overhang: spaces/auto` 覆盖表意空格、制表、不换行空格、对开括号的一半与全角中点的四分之一；emphasis 标记改为**纯墨迹**，站在文字内容边外，不占行；竖排 emphasis 按物理左右取边 | CSS Ruby 1 §3.6、CSS Text Decoration 3 §3.3；全部先在 Chrome 探针上量过（`ruby` 的 `rt` 紧贴内容边，`line-height: 5` 也不会被 annotation 撑开） |
| `5fa9d323`、`f0066f0b`、`bf86851c`、`77b4d711`、`47e4baff` | body 的 `overflow` 在根不裁剪、不含 containment 且 body 自己也不 containment 时交给 viewport；方形 outline 外扩时保持直角；`WideKeywordDeclarationFactory` 的声明缓存改为 `ConcurrentDictionary`；`@namespace` 写在规则之后时只丢这一条，后面的规则继续解析；ruby base 的基线在它自己的 band 内测量 | CSS Overflow 3 §3.3、CSS Contain 1 §2；CSS UI 4 §2.2；CSS Namespaces 3 §3。outline 与 overflow 的判定都用 Chrome/引擎对照页验过 |

**这一轮最贵的一课（0.69 像素）**：给 ruby base 从"整行盒"改成"自己的 band"时，只改了 `BorderBox` 的高度和位置，`Baseline` 却仍从 **line.Height** 起算。带 ruby 的参考页里行高 80、band 80，看起来没事；可五条 80 像素行里混入 fallback 字体后，行盒是 80.69，base 的 band 仍是 80，基线于是整体下移 0.69 像素。全量立刻红 **58 对** text-emphasis / ruby-overhang 对照——每一对都是"test 画了颜色、ref 在同一像素是白的"。教训：**band 一旦小于 line，基线也必须换算到 band 自己的坐标里**；`flow.BlockOffsetFromLeft(band, baseline - bandTop)` 才是对的。另一个操作教训：只跑 `--no-build` 的旧 DLL 时，这种 0.69 的差异会被误读成上一轮的问题，改完引擎必须先 build 再记分。

**单元测试跟着语义走**：旧的 `EmphasisMarksTheTextWithoutTakingRoom` / `AMarkedColumnKeepsItsStripOnTheSideTheMarksStand` 写的是"标记占一条 strip、行因此变高/变宽"，那是我们旧实现的行为，不是规范也不是 Chrome。这轮把它们改成"行高仍是 40、标记落在内容边外的 5 像素里"，并新增 `NamespaceRuleTests` 盯住"错位的 `@namespace` 不拖死整张表"。

**下一步（失败集 2313，按便宜程度排）**：
- **95 对 ≤120 像素的近似失败**仍然是最便宜的一批，优先按目录聚类。
- `@supports` / 条件颜色族（`#ffffff → #008000`、`10000 pixels differ first at 8,50`）。
- text-emphasis 竖排 `005d–g / 006d–g`（约 70–120 像素）。
- ruby 的 tab / 竖排 overhang；`gecko/table-anonymous-boxes`；`gecko/bugs` 中非 JS 的部分。

## 15. 2026-10-28 深夜轮：真正的基线、justify-content 的四种边、内容关键字与竖排 flex

起点 **wpt 4246 / gecko 1746**（失败集 2313）；本轮结束 **wpt 4287 / gecko 1746，失败集 2272**：**修复 41 对、回退 0**。单元测试 1898 → **1903**，全绿。

| commit | 做了什么 | 依据与取舍 |
|---|---|---|
| `b0d435c7` | 有块级子元素的 inline-block 取**正常流最后一行**的基线：在独立引擎里试排一次，再按相对位移还原，避免溢出子元素被排两遍；`overflow` 裁剪的盒子仍回到底边；inline-flex/grid 容器取**块起始边最近、行内主起始边优先**的那条 flex 行；行盒高度改为按 `vertical-align` 之后的盒子重新求并集，文字 run 仍保留字体自身的上下沿 | CSS 2.1 §10.8.1，CSS Flexbox 1 §8.5；Chrome 探针逐页核对（`wrap-reverse` 时取物理上边那行，column-reverse 时取主轴起点那项） |
| `90e30027` | `justify-content` 把 `flex-start/flex-end`、`start/end`、`left/right` 分成四组语义：前两者随反向主轴移动，`start/end` 指容器自己的行内/块起始边，`left/right` 是物理边且主轴不平行时退化为 `start`；AngleSharp 的 `FlexContentMode` 无法区分，改为先读声明文本 | CSS Box Alignment 3 §5.2，CSS Flexbox 1 §8.2；8 对 `flexbox_justifycontent-*` 全绿 |
| `9dc6b947` | 竖排 flex 容器的 `width:auto` 是**块尺寸**，由内容决定而不是填满包含块；竖排 flex item 的 cross 尺寸用 `Measure` 的物理宽而不是 inline 的 max-content | CSS 2.1 §10.6.3，CSS Writing Modes 3 §6；6 对 `css-flexbox-row*` 与 `css-flexbox-test1` 连带修好 7 对 |
| `336e4ddc` | `min/max-*: min-content/max-content/fit-content` 真正参与 flex item 的尺寸约束：按长手名命的物理轴取内容尺寸；同一盒子的关键字不能反过来限制它自己要问的测量；min 大于 max 时 min 胜；竖向 item 的内容主尺寸按夹紧后的 cross 宽测 | CSS Sizing 3 §5.2，CSS 2.1 §10.4；6 对全绿 |
| `a616b5df` | 列向 flex item 的自动最小尺寸：主轴是块轴时取内容堆叠出的高度（列向嵌套容器取自己项目组成的那一行，表格/网格/替换元素暂不设底），没有声明块尺寸的盒子直接用测量结果 | CSS Flexbox 1 §4.5；4 对 `flex-minimum-height-flex-items-*` 全绿，过程中修掉 11 个回退（关键是“自己的主轴是块轴”不能写成“是 column”，否则把 grid-gap 参考页里的 flex 行也当成列） |

**这一轮的两条方法教训**

1. **“最后一行”不能靠 `HasBlockChildren` 一票否决**：这只说明盒子有块级子元素，不说明它没有自己的行。改为试排一次取最后一行之后，`multi-line-vert-001/002`、`single-item-001a/b`、`list-and-block-in-inline` 一起转绿；试排必须放在**独立引擎**里，否则绝对定位子元素会被登记两次、相对位移也会被算进基线。
2. **行盒是“对齐之后”的并集，不是“对齐之前”的并集**：旧实现把对齐前的 ascent/descent 当作下限保留，`vertical-align: top` 的块因此把对齐前的下半截也算进行高。改成按 shift 之后的盒子重算后，`1156021-text-indent-percent` 与 `static-inside-inline-block` 回绿；但文字 run 仍要保留字体自身的上下沿（上标那一课）。

**当前还剩下的近失清单（≤120 像素，88 对）**里，值得下一轮先看的：

- `css-text-decor/text-decoration-inset-*` 族（约 12 对，8 到 982 像素）与竖排 underline/overline 的 1 像素级边缘（`text-decoration-thickness-vertical-001`、`text-underline-offset-vertical-003`）。
- `css-lists/counters-scope-001/004`：嵌套作用域里的 `counter-reset` 泄漏到了兄弟（期望兄弟读祖先的 1，我们读 3）。
- `gecko/table-anonymous-boxes`（约 20 对）与 `gecko/bugs` 里的非 JS 部分。
- flex 的 `aspect-ratio` 尺寸转移（6 对：`aspect-ratio-intrinsic-size-003/008/009/010`、`flex-item-transferred-sizes-padding-*`），需要在主轴解析之后把 cross 的确定尺寸转回主轴。
- `@supports`/条件颜色族尚未系统排查（本轮之前的子 agent 因额度中断）。

## 16. 2026-10-28 后半夜轮：aspect-ratio 的跨轴传递，与 @supports 的读法

起点 **wpt 4287 / gecko 1746**（失败集 2272）；本轮结束 **wpt 4304 / gecko 1746，失败集 2255**：相对 2313 基线 **修复 58 对、回退 0**。单元测试 1903 → **1910**，全绿。

| commit | 做了什么 | 依据与取证 |
|---|---|---|
| `a5a3955b` | flex item 的 `aspect-ratio` 跨轴传递：主轴 auto 而 cross 已定（自身声明或被 line 拉伸）时，主轴由 cross 反推；列向则由主轴推出 cross；容器的 intrinsic 主轴也算这条传递（row 看容器确定的 cross 高，column 看 item 声明或 `min-height` 兜住的主轴） | CSS Sizing 4 §5.1；`aspect-ratio-intrinsic-size-003/008/009/010` + `flex-item-transferred-sizes-padding-{content,border}-sizing` 6 对全绿 |
| `f716c133` | `@supports` 的四条读法：未知函数是 `<general-enclosed>`（值 false，而不是"读不出来"，所以 `@supports not unknown()` 为 true）；补 `font-format()` / `font-tech()`；`or(` / `and(` 是 function token，不是关键字；把 `::placeholder`、`::file-selector-button`、`::details-content`、`::-webkit-slider-thumb`、`::-webkit-slider-runnable-track` 登记进伪元素工厂 | CSS Conditional 4 §3.1、CSS Syntax 3 §4.3.9；每条都用 `CSS.supports()` 在 Chrome 上量过（`not (not(foo: baz))`=true、`font-format(opentype)`=true、`font-format("opentype")`=false、`((background-color:red) or(background-color:green))`=false）。9 对 `at-supports-*` 转绿 |

**deps 是 submodule，改动要分三步**：先在子模块里 commit，再在父仓 `git add deps/... && git commit` 记 gitlink。**绝对不要在父仓用 `git stash`**——这次 `git stash push -- deps src` 报"无改动可存"（deps 是子模块、src 恰好干净），紧接着的 `git stash pop` 就把仓库里一条**陈年的 devel stash** 弹进工作树，把 `GridFormattingContext.cs` 撞成 unmerged。用 `git reset --hard HEAD` 复位（自己的都已 commit），子模块里的工作不受影响，两条旧 stash 也还在列表里。要验证"某条 deps 测试是不是我改坏的"，就在子模块内部 `git -C deps/... stash push -u` / `pop`。

**记下来免得重复侦察**：`AngleSharp.Css.Tests` 里 `CssLetterSpacingNormalLegal`、`CssWordSpacingNormalLegal`、`CssInitialLetterInvalid`、`CssSheetWithUnquotedUrlContainingCurlyBrace`、`ValueKeepsTheSemicolonsInsideTheGroupsItOpens` 这 5 条在借来的 deps 里**本来就红**。本轮改写了其中一条 deps 测试 `SupportsNegationNeedsWhitespaceBeforeItsCondition`：它原来断言 `@supports (not(foo: baz))` 整条规则被丢弃，而规范和 Chrome 都是"规则留下、条件为 false"（因为 `not (not(...))` 要为 true）。

**剩下的 `@supports` 尾巴（5 对）**都属"这个位置不许出现这条 at-rule"：`at-supports-content-001` / `at-media-content-001`（条件块里的 `@namespace` 不该登记，而**未声明的前缀应当只作废那一条规则**——我们在匹配期才解析前缀，解析器无从知道命名空间表，需要在收集样式规则时按表校验 selector 里的 `prefix|`）、`at-supports-045`（`@supports` 之后再出 `@import`/`@namespace` 要作废后续）、`at-supports-namespace-002`（`selector()` 里的前缀只在有效 `@namespace` 之后可用）、`at-supports-048`（样式规则嵌套里的 `@supports`）。`selector(:is(:foo,.a))` 要为 false：Chrome 用的是"严格选择器校验"，而 `:is/:where/:has` 是**函数型**伪类，不在工厂表里——试过一次在 `selector()` 里扫未知伪类，`at-supports-selector-picker` 立刻红，已回退；正解是给核心解析器加一个 strict 开关。

**近失（≤120 像素）还剩 88 对**，但绝大多数是 1–2 像素的圆角/装饰抗锯齿（如 `contain-paint-clip-017/018` 只差 1 像素的 #fcfaf9/#fcfdfc），另有 8 对是 `line-breaking`/`word-break` 的小语种断行（Javanese/Thai/Lao），需要真正的 UAX #14 断行表，都在结构化缺口一档。

## 17. 2026-11-03 轮：表格跟着书写模式走，col 拿到自己的带

**做掉的两件事**（一个提交口径：表格的轴）

1. **表格有了"内联轴/块轴"的概念**。之前 `TableFormattingContext` 整个只认物理宽高：列永远沿 x、行永远沿 y，`writing-mode` 只对表格盒子自己生效、对网格无效（`table-progression-vrl-001` 差 30678 px，网格整个没转）。现在网格全程在逻辑坐标里算（列=内联，行=块），只在两处落到物理：
   - 入口：`Measure`/`MeasureIntrinsic` 从物理约束里取出内联/块可用尺寸，声明尺寸按轴读对应的物理属性（竖排表的"列宽"由 `height` 决定，行轴由 `width` 决定）；`MeasureConstraints.FitHeight` 是为竖排单元格新增的入口。
   - 出口：所有 `LayoutBox` 用 `WritingModeInfo.Map(content, inlineOffset, inlineSize, blockOffset, blockSize)` 落位，row/rowgroup/cell/caption/非表部件子节点无一例外；`border-spacing` 的两值按轴选（`TableBuild.InlineSpacing`/`BlockSpacing`），insets 同理。
   - `direction: rtl` 不再单独打补丁：`Map` 内联轴反向，collapse 的边框按"内联始/终、块始/块终"四侧读写（`InlineStartSide` 等），顺带把 RTL 表 `border-left:6px` 与邻居 `border-right:2px` 正确合到同一条线上。
2. **`col`/`colgroup` 拿到自己那几列的带**（CSS 2.1 §17.5.2）。两个缺陷叠在一起才看得见：`<col span=2>` 原先只记 1 列（`CollectColumnHints` 按元素不按列），而且列元素根本不生成框，背景无处可画。现在 `LayoutKind.TableColumn` 有框（只画背景，`SuppressBorder`——线的边框归单元格画），`ColumnBoxes`/`ColumnGroupBoxes`/`ColumnHints` 三者按列对齐，`colgroup span=N` 无子元素时也算 N 列。

**读数**（Ahem 已装，HEAD `94ad8ac2` 之上未提交）

| 语料 | 前 | 后 |
|---|---:|---:|
| `wpt/css-writing-modes/` | 剩 50 | **剩 40** |
| `wpt/css-tables/` | 剩 32 | **剩 30** |
| `gecko/writing-mode` | 剩 20 | **剩 19** |
| `wpt/CSS2/` | 68 | 68（无回退） |
| `wpt/css-text-decor/` | 54 | 54 |
| `gecko/tables` | 0 | 0 |

单测 **2196 → 2198**（新增 `ATableThatReadsRightToLeftMirrorsItsColumns`、`AVerticalTableTurnsItsGridThroughTheWritingMode`、`AColumnCoversTheColumnsItsSpanNames`），硬断言 63 全绿。`table-progression` 10/10 清零。

**正交单元格（本簇已做一半）** 单元格自己的书写模式可以与表格不同（`table-cell-valign-*` 是竖排表里的横排格，`table-cell-align-005/006` 是横排表里的竖排格）。三处按格的 `Box.Flow` 走而不是按表格的：单元格子节点的 containing block（物理对 + `containingIsVertical`）、`vertical-align` 的移动轴、以及**行轴尺寸**——正交格的自身内联轴可能正好落在行轴上，而块布局沿自己的内联轴是"填满所给"而非"问内容"，给 0 就报 0，于是行高塌成 0（valign 那族原本 `table 8x316`、格子 `2x102`）。此时改用格声明的行轴尺寸，无声明则用内容的 max-content 建议。做完：valign-001/002/003 从 1738 px 掉到 104 px、align-001/003 从 9324 掉到 800，**但还没有一对翻绿**（104 px 是渐变硬边那一行的次像素相位差，超出页面自报的 `totalPixels=0-1` 容差）；align-005/006 与 table-cell-002 的像素数反而变大，说明这两对在正交格的行轴上还没量对——下一步从这里查。

## 18. 2026-11-03 轮续：正交格量对了轴，`:dir()` 顺着树找

§17 留下的"正交格还没翻绿"这一半做完了，根因是三处**轴读错**，都不是"单元格特殊"。

1. **列要的是格"跨过自己那行"的尺寸**。横排表里的竖排格：列沿 x，而格自己的内联是 y。`MeasureContentIntrinsic` 报的是沿**格的内联轴**的建议值，直接交给列就把"文字有多长"当成"格子有多厚"（`table-cell-001` 因此 80 宽而不是 20）。新增 `CrossIntrinsic`：按格自身的流做一次无约束 `Measure`，再用 `Flow.InlineExtent` 在**表格**的内联轴上取值。
2. **交给格的 containing-block 方向旗标属于表格，不属于格**。`ForContainingBlock(size, containingIsVertical)` 说的是"包含框怎么流"，此前处处传 `cell.Box.Flow.IsVertical`：同流时看不出差别，正交时就把表格的容器说成了竖的，格于是认为"内联由容器给"，而容器在那方向 nothing to hand down（0），横排表里竖排格的行高塌成只剩 padding（`orthogonal-cell-001` 五张表只有一张对）。改成传 `flow.IsVertical`（`MeasureCellHeights`、`ArrangeCell` 各一处）。
3. **跨流的块盒由内容决定自己那条线的长度**。`BlockLayout.MeasureColumn` 的 `fillsInline` 只问"是不是常规块级盒"，正交时照旧按"填满容器"算。加上 `context.ContainingIsVertical == flow.IsVertical`，并让 `MeasureColumnChildren` 额外回报子盒沿内联轴的最大距离 `FlowedInline`（只在 `height:auto` 且不填满时接住）。这条也解释了 `available-size-*` 一族为什么一直量不满。

**`vertical-align` 在反向块轴上要取最小坐标**：`vertical-rl` 的格块轴从右往左，内容的"末端"是最小 x；原来一律 `Math.Max`，把它当成没占地方，整格宽度都成了余量，内容被推出格子（`table-cell-align-005` 16640 → 6400 → 绿）。

**`:dir()` 是问树的，不是问某一个元素的**。vendored AngleSharp 的 `DirFunctionState` 只比对元素自己的 `dir` 属性，于是 `<tr dir=rtl>` 里的 `td.start:dir(rtl)` 整条规则作废、背景不翻转（`table-cell-align-001..005` 全卡在这）。按 HTML §3.2.1.3 沿祖先链取最近的 `ltr`/`rtl`，一路没有就是 `ltr`；`auto`（以及默认的 `bdi`）留给双向文本解析器定方向，选择器不猜。改在 `deps/AngleSharp`：新增 `DirectionNames`、`ElementExtensions.GetDirectionality`，`CssSelectorConstructor` 用它。

**转倒文本的 skip-ink 墨框起点在基线上**。`SkiaPaintExecutor.Beside` 先把 blob 的墨框按 `Bounds.X` 平移、再绕 `baselineX` 旋转，两个原点差了一个 `axis`，整条列跟着错位。于是要求"不画线"的 `text-decoration-skip-ink-sidewayslr-002` 露出 16 px 线头，而 `-sidewaysrl-002` 一直不绿。平移改用 `baselineX` 之后两对都按页面说的画（这条也是笔记 §7 里 `skip-spaces` 一族"被 blob 内落位卡住"的同一处）。

**读数**（Ahem 已装）

| 语料 | 前 | 后 |
|---|---:|---:|
| `wpt/css-writing-modes/` | 剩 40 | **剩 31** |
| `wpt/css-tables/` | 剩 30 | **剩 28** |
| `wpt/css-text-decor/` | 剩 54 | **剩 53** |
| `gecko/writing-mode` | 18 | 18 |
| `wpt/css-text/` | 95 | 95（无回退） |
| `wpt/css-ruby/` | 32 | 32（无回退） |
| `wpt/CSS2/` | 68 | 68（无回退） |

翻绿：`table-cell-001`、`table-cell-002`、`table-cell-align-001/002/003/004/005`、`orthogonal-cell-001`、`two-levels-of-orthogonal-flows-fixed`、`table-intrinsic-size-003/004`、`text-decoration-skip-ink-sidewaysrl-002`。单测 2198 → **2204**（`AColumnDownThePageHoldsWhatItsCellAsksAcrossItsLine`、`ARightToLeftCellStandsItsContentOffTheEdgeItsOwnBlockEndIs`、`DirPseudoClassTests` 四则），硬断言 63 全绿。

**教训（工具层）**：PowerShell 的双引号 here-string 会把 `` `t ``、`` `n `` 等当转义序列吃掉，往中文文档里追加带反引号的 Markdown 会被静默改写（`table-cell-001` 变成 TAB+"able-cell-001"）；改用 `apply_patch` 或单引号 here-string。另外该文件在库里是 LF，用 `WriteAllText` 写成 CRLF 会让 `git diff` 显示整篇重写——`--ignore-cr-at-eol` 能确认自己只改了 55 行。

**正交格剩下的线索**：`table-cell-align-006`（3200，竖排表里横排格 + `padding-bottom` 时格内 `div` 的落位）、`three-levels-of-orthogonal-flows`（34080）、`percentage-padding-in-shrink-to-fit`（435600，收缩适配里百分号 padding 的基准）、`inline-box-orthogonal-child-with-margins`（66730）。`table-cell-valign-001..004` 仍卡在渐变硬边那一行的次像素相位（104 px，超出页面自报的 `totalPixels=0-1`），和装饰族的相位问题是同一条线。

## 19. 2026-11-03 轮续：装饰线铺在整像素上，`skip-spaces` 补上

**装饰线要铺在整像素上**（`SkiaPaintExecutor.Band` 走 `SnapToDevice`，与边框侧、背景同一套规则）。`text-decoration-inset-*` 那一族借用页的参考做法是"三根错开的短线叠成一根长线"：软边叠三次合成一个颜色，单根的一次是另一个颜色，只有线本身没有软边时两种画法才可能相同。headless Chrome 实测把 32px 的下划线铺成整整三行实心（y=102..104），我们当时是 103.4..106.6（上下各一行软边）。改完 **inset 族 12 对当场翻绿 6 对**，并顺带修好 `text-decoration-thickness-vertical-001`、`text-underline-offset-calc`；余下 6 对从 68~982 px 掉到 6~693 px。

**转倒文本的基线在盒子正中，不跟着 `line.Ascent` 走**。`text-emphasis-position-property-005*`/`006*` 共 16 对（竖排 + `text-orientation: sideways` 的正文配强调符，参考页用 `<ruby>` 复刻）。竖排表那批先靠"正交格"三处修好；横竖混对（005d..g / 006d..g）还差 1~2 px：`Marks` 与 ruby 注疏都用 `line.Ascent` 定"文本外边"，而画转倒字形时 `Axis()` 是按盒子正中 ± `(ascent-descent)/2` 落的 —— 两套口径。当行的 ascent 来自**回退字库**（CJK 字形解析到的 face 与页面声明的 serif 度量不同）时两者相差 0.69 px，镜像两侧一正一负。改成：转倒流的注疏与强调符一律用 `(line.Height + ascent - descent)/2`（与 `Axis` 同一口径）→ **16 对全绿**。

**`text-decoration-skip-spaces` 之前压根没实现**（属性名没注册、值没人读）。用 Chrome 量过默认值：不声明时空格照样画线，所以页面注释里"按 L3 这是默认行为"不作数，得显式实现。链路：`WideKeywordDeclarationFactory` 注册名字（AngleSharp 无文法，否则整条声明在 parse 期就丢了）→ `ComputedStyle.ResolveSkipSpacesFused` 读 `none | [start||end] | all` → 值挂在 `DecorationInsets.Spaces`（与 inset 同一条通道，因为把两端往里拉的正是那次遍历）→ `LayoutEngine.Trim`：段首 run 的前导空白宽度并入 `start`、段尾 run 的尾部空白并入 `end`，字母之间的空格一律不管。单测 `ARuleAskedToSkipItsEndSpacesStopsAtTheFirstLetterUnderIt` 断言 trim 恰等于两侧空白的宽度。

**跨空格的 kern：一整串与被元素切成几段必须落在同一处**（`src/Hutu.Skia/SkiaTextShaper.cs`）。skip-spaces 实现完还剩 1263~1272 px 的差异，根因不在那个属性：`        ABCDEF        ` 整串一个 blob 时字母落在 62，被 `<span>` 切成三段时落在 64，而 Chrome 两种写法都在 64。HarfBuzz 对"空格 + 下一个字形"照样答 GPOS 字偶对，浏览器在词边界把查找停住（Blink 用 word-extents）。这里等价的做法是**按空白切段分别 shape，再把各段字形按各自笔位拼回去**：段内字偶照旧，跨空格不成对。`ShapeRun` 与 `ShapeClusters` 两条路径用同一套切法（否则字距按字形落、字形按段落），从右往左写的整串不切——各段谁前谁后是文字的本事，不是这个切分能定的。**skip-spaces 5 对当场翻绿**，`wpt/css-text` 95、`css-ruby` 32、`CSS2` 68、`css-tables` 28、`gecko/writing-mode` 18 全部无回退。新单测 `CuttingASentenceAtItsSpacesLeavesItsLettersWhereTheyWere` 直接比对两种写法的落墨列，改之前差 2 px。这条大概也是 `letter-spacing-cursive`、`grow-consistent` 一类零散对的同一条根。

**读数**（Ahem 已装；四块语料合计 231 → 181（见 §20））

| 语料 | 本轮开始 | 现在 |
|---|---:|---:|
| `wpt/css-text/` | 95 | 95 |
| `wpt/css-text-decor/` | 54 | **24** |
| `wpt/css-writing-modes/` | 50 | **31** |
| `wpt/css-ruby/` | 32 | **31**（见 §20）
| `wpt/css-tables/`（旁证） | 32 | **28** |
| `gecko/writing-mode`（旁证） | 20 | **18** |
| `wpt/CSS2/`（旁证） | 68 | 68（无回退） |

单测 2196 → **2207**，硬断言 63 全绿。

**工具层教训（第二次踩）**：`apply_patch` 千万别写占位内容 —— 我一次误写 `{ ... }` 破坏了 `TableLayoutTests.cs` 的 helper，另一次破坏了 `TextDecorationPaintTests.cs` 只能 `git checkout --` 回滚（那是我自己几分钟前的改动，才敢这么处理）。动手前先用 `Select-String` 核对精确上下文。

## 20. 2026-11-03 轮续：外面的浮动不许钻进 inline-block 的格式化上下文

`css-ruby` 里 `ruby-with-floats-001/002/003` 三对同一条根：测试页把一个 `<ruby>` 直接放在浮块旁边，参考页把同一个 ruby 裹进 `display:inline-block`。我们的参考页把嵌套那一行**压到浮块下边**（`line 100,50` 而不是 `100,0`），外层盒子因此比它的内容高出一截（85.44 而不是 35.44）。CSS 2.1 §9.4.1／§9.5：inline-block 自成一个 BFC，**外面**的浮动既不缩短它里面的行，也不把第一行压下去——是它整块在浮块旁边让位，而这一步线在放它之前就已经答过了。

`LayoutContext.ForContainingBlock(size, containingIsVertical)` 原样把 `Floats` 带进下一个上下文，于是嵌套的行仍拿得到外层那条 `FloatBand`。修在两处：`BlockLayout` 内联内容那支交给 atomic 的上下文 `.WithFloats(null)`，以及 `InlineContext` 构造里 `Atomic` 一律清掉。**只清一处不够**——把两处分别拆掉试，001 和合成用例都还会露出那 50 px；两条 pass（measure 与 arrange）对同一件事不能给两个答案。

新增单测 `AFloatStopsAtTheFormattingContextAnInlineBlockRoots`（`FloatLayoutTests`）：浮块 50×50 + 一个两行的 inline-block，断言盒子高 20 且首行在盒顶；任一处清除拿掉都会让它红（70 高、首行 y=50），是有效守卫。

**这轮结果**：`ruby-with-floats-001` 翻绿（`css-ruby` 32 → **31**），其余三块与旁证模块无回退。002/003 仍差 530 px——那两对的另一半在**线盒自己**：浮块的顶在线顶之下、却和线的高度相交时，Chrome 把整条线缩到浮块右边（x=100），我们按线当时的 0 高去问 room，inset 只取到 50。下一步从这里查。

## 21. 2026-11-03 轮续：专名号的过悬把"身边的空格"逐字让出来

`css-ruby` 里 `ruby-overhang-spaces-*` 是最大的一族（水平 8 对 + 竖排 4 对）。规范 CSS Ruby 1 §5.1 把 `spaces` 允许的过悬对象列成一张表：**保留空白、U+00A0、其他空格分隔符、全角开标点起始侧的一半、全角闭标点结束侧的一半、全width中点两侧各四分之一** —— 三条我们自己都写歪了。

**一、"一半/四分之一"是逐字向外累加的，不是只看贴住的那一个字符**。旧实现是"先数贴着的空格串，串外就不看了"（注释里还写着理由："空格后面的那个标点不是注疏要盖的东西"）——这句判断是错的：注疏可以连着盖过 `　」` 这一串。按字符向外走、走到第一个不肯让的字符才停之后，`-016`、`-017` 当场翻绿（`　`(20) + `」` 的一半(10) = 30，正好是 Chrome 的量）。

**二、被 `text-spacing-trim` 裁掉的半边不再让第二次**。规范每一项都带 "unless it has been trimmed by text-spacing-trim"。`-021` 的 `））`：Chrome 实测第一个 `）` 宽 10（被裁）、第二个 20，注疏只从第二个拿到 ½×20=10，扫到被裁的那个就停 → 盒子 160 ✓。我们的 `IsFullwidthPunctuation` + `TextSpacingTrim.TrimsOpening/TrimsClosing` 正好能答这一个问题，邻居取"朝专名号那一侧的字符"（贴住注基的那个字符，其邻居是注基本身的首/尾字符）。

**三、中点的四分之一按整宽算，不按字形宽**。`-019` 用 `&#x00B7;`（不是 `・`），Chrome 里它只有 5.2 px 宽，可过悬仍给了 5 = **¼em**。所以份额一律乘"一个全角字符在线上的宽度"（`FullwidthRoom` 取 U+3000 的推进，横竖各随其流），而不是字符自己的推进。

**四、制表符让出的是"到下一个停靠点的距离"**（CSS Text 3 §4.2），这只有线自己答得出。旧实现把它当一个字形量（serif/20px 答 8.43 px，实际停靠距离 20 px）——`-004`、`-006` 差的就是那 5.79 px。补法：两条 pass 都记下每个 item 的**起点**（intrinsic 用 `maxContent + trailing`，线用 `builder.Width`），从注基往回走时先用起点量出前缀、得到空格段的真实位置，再按停靠算；往后走时起点是**注疏读到的位置**（`ReadingEnd`）而不是盒子边缘——Chrome 的 `-013`/`-017` 两处实测都只和这一条一致（017 的第二个制表符宽 10 = 从注疏右缘 190 起算；若从盒子右缘或注基右缘起算都不对）。

**五、专名号长大时，基不是居中，是"近端亏空 + 基 + 远端亏空"**。`-017` 两侧room 不等（50 / 40），Chrome 把基落在 `盒子起点 + 近端亏空`，注疏再以基为心 —— 我们原来的 `RubyAligned` 居中会把基推后 5 px，于是"总宽对了、画面还是错的"。现在 `RubyHang` 把近端亏空存进片段，注疏盒子改成以基的位置为原点、以基宽为房间（`AnnotationBox` 的两个参数换了），对称亏空时与旧行为逐位相同，所以 `ruby-overhang-spaces-align-*` 五对没动。

**注基里的制表符**也顺手改了：`PairWidth` 见到制表符就走 `WhiteSpaceWidth`（`ruby-tab-in-base-001` 的基从 50 变 100 ✓ 与参考页 `padding-left: 75px` 一致）。那三对仍差：画的时候整串 `\tY` 是一个 blob，制表符按字形的 25 px 落位，而线给了 75 ——普通 run 是在制表符处切成片段的，注基没有。

**结果**：`css-ruby` 31 → **27**（`-004`/`-006`/`-016`/`-017` 翻绿），`-011` 从 7120 px 降到 1428 px。`Hutu.Tests` 2207 → **2210**（三条守卫：跨空格继续拿标点、被裁的标点不再让、制表符按停靠点让），硬断言 63 全绿，`css-text` 95 / `css-text-decor` 24 / `css-writing-modes` 31 / `css-tables` 28 / `gecko/writing-mode` 18 / `CSS2` 68 全部无回退。

**下一刀在哪（已经定位，不在过悬本身）**

* `-021`：参考页用 `font-family: halt-font`（字形自带 `halt`）。Chrome 的 `text-spacing-trim: space-all` 会**把字体自带的宽度特征关掉**（那两个 `<span>` 里的 `）（` 回到 20 px），我们只在 `HasExclusiveFeature` 里看作者写的 `font-feature-settings`，于是参考页的注基少 20 px。改在 `space-all` 时给 shaping 追加 `"halt" 0, "vhal" 0, "palt" 0, "vpal" 0`。
* `-019`：`·` 在 `あ·` 里比单字多 2.5 px，在 `·　` 里不多——`text-autospace` 的边界判断在跨 item 时不一致，测试页/参考页因此各差 5 px（113.36 vs 118.36）。
* 竖排四对：盒子两侧宽度都对（140），差在注疏与注基的落位，先从 `vertical-001` 的 702 px 那一条线看。
* `-013`/`-011`：制表符的"上一趟宽度"。Chrome 把长大后的盒子整体右推时，**不重算**后面那个制表符的宽度（013 实测 8.83 = 从注基右缘 51.17 起算，而它画在 62.34 上）；我们按实际起点重算，于是差一个停靠周期。要一致就得把"注疏读到的位置"同时也当作画的落位来问。

**工具**：这一族靠两个新脚本定位的 —— `build/tmp/measure.ps1`（把 `measure.js` 注入副本页，用 headless Chrome `--dump-dom` 打印每个字符的 `Range` x/w 与 ruby/rt 的实际盒子）和 `build/tmp/chrome-x.ps1`（截图后扫橙色/蓝色边框的列）。注意两件事：本机已有 Chrome 会话时**必须**给 `--user-data-dir`，否则 `--dump-dom` 一句都不输出；PS 5.1 的 `Get-Content -Raw` 按 ANSI(GBK) 读 UTF-8 会把 CJK 读成乱码，要用 `[IO.File]::ReadAllText(..., UTF8)`。

## 22. 2026-11-03 轮续：一段文字里的空格，线只问到第一个为止

`css-text/white-space` 那 23 对里 `trailing-ideographic-space-break-spaces-001/003` 与 `break-spaces-with-ideographic-space-001` 同一条根。测试页 `ああ　　　　ああ` 放进 3 字宽的盒子，Chrome 量出来是三行 `ああ　` / `　　　` / `ああ`，我们给的是四行 `あ` / `あ　　` / `　　あ` / `あ`。

分块把 `あ` 和它后面的四个全角空格留成同一段（`LineBreaks.MayBreak` 在 ID×BA、BA×BA 都不给断点，到 BA×ID 才断），于是线在段首问的是"**整段**要不要放得下"：`asks = width + trailed + mark`，这一段 5 个字符 = 80 px > 剩下的 32 → 判不下 → `NextLine()` → 第二个假名整段掉到下一行。可 §4.1.3 给 `break-spaces` 的断点是"**每**一个保留空格之后"，也就是说这段内部本来就有合法的断行处，段首不该按整段问。同一条规则在拉丁文那侧一直是对的：`XXXX　　XX` 里 Chrome 也只让第一行拿到 `XXXX　`（溢出到 80，不是 96），因为它的"整段"只问到第一个空格为止。

改法（`AskWidth`）：`stepsSpaces` 为真且段内确有可断的空格时，段的"要价"只算**到第一个保留空格为止**（前缀用 `Measure`，那一个空格用 `WhiteSpaceWidth`，制表符仍按停靠点算），其余照旧整段。于是 `ああ　　　　ああ` 第一行 `ああ　`、第二行 `　　　`、第三行 `ああ` ✓；拉丁 `XX　　　　XX` 与 `XXXX　　XX` 逐字符位不变 ✓。**`wpt/css-text` 95 → 92**。

守卫：`AKanaKeepsTheFirstOfTheSpacesBehindItOnTheLine`（`BreakSpacesWrapTests`，假测量器 5 px/字符、盒宽 20 px）断言两行、首行铺满 20 px；把 `AskWidth` 换回 `width + trailed + mark` 立刻变红（四行），是真守卫。

**顺带定位到但没动的三件事**（都贴着 `break-spaces` / ruby 这一族）

* `trailing-ideographic-space-break-spaces-008`：`XXX<span>　</span>X` 放进 3 字宽，Chrome 让空格留在第一行末尾并溢出（第 4 格在 83..108），我们把空格挪到了第二行；同一个文本节点里（`XXX　X`）我们是对的。走过的两条死路要记下来：① 把后一个 item 开头的空格也算进 `trailed`（词与空格跨元素仍是一体）——对 008 无效，动的是 4505 那条"放不放下"的判据，不是把空格吐回去的那条；② 在 `CanBreakBefore` 里禁止"跨元素边界的 run 以空格开头时断行"（§4.1.3 的机会在每个空格**之后**，不在第一个之前）——008/#d 两例当场对上了 Chrome，但 `break-spaces-before-first-char-007..011/016/017` 与 `break-spaces-before-first-ideographic-char-007..011/016/017` 共 15 对立刻变红（这些页面正是要在第一个空格之前断）。差别一定还在 `beforeFirst`（`WrapsBeforeSpaces = overflow-wrap: break-word|anywhere || line-break: anywhere`）与该不该用 `CanBreakBefore` 的宽授权之间，需要按这 15 对再定一次规则；`steps` 的判据里加 `trailed` 同样会碰坏这一族，已回退。
* `ruby-overhang-spaces-011/013`：跟在长大后的盒子后面的那个制表符，Chrome 用的是"**上一趟**按注疏右缘算出的宽度"，画的时候整体平移而不重算（013 实测宽 8.83 = 从 71.17 起算，而它画在 62.34 上）；我们按实际起点重算，于是要么差一个停靠周期，要么在两个自洽解之间摇摆。要对齐就得让线后的空格段也按"注疏读到的位置"量一次宽度（并接受它与盒边不连续）。
* `ruby-overhang-spaces-019/021`：`text-autospace` 在跨 item 边界多给的 2.5 px（`あ·` 比 `·` 单字多 2.5）与 `text-spacing-trim: space-all` 应当关掉字体自带的 `halt/vhal/palt/vpal`（Chrome 里那两个 `<span class=space-all>` 的 `）（` 回到 20 px，我们仍是 10）。这两条都在测量侧，与过悬规则无关 —— 过悬那一侧我们已经和 Chrome 逐位对齐。

**读数**：`wpt/css-text/` 95 → **92**、`wpt/css-text-decor/` 24、`wpt/css-writing-modes/` 31、`wpt/css-ruby/` 27；旁证 `wpt/css-tables/` 28、`gecko/writing-mode` 18、`wpt/CSS2/` 68 —— 全都不回退。单测 **2211**（这两轮新增 4 条守卫）全绿，硬断言 63 通过 / 1 跳过。四块目标语料合计 231 → **174**。

## 23. 2026-11-03 轮续：`normal` 的行高不是上限，小块字号不许把线压扁

`white-space-zero-fontsize-001/002` 两对同一条根，顺带翻绿 `text-autospace-elements-002/004/006` 三对。测试页是 `pre { font-size: 0 }` 里放两个 `span { font-size: 12px }`，参考页把 pre 改成 12px —— 断言：保留的换行在字号为 0 时仍然算断行。我们的两条线高度是 **0**：`line pre … x0`，两行叠在一起。

根因在 `Build` 收线高那一段。strut 的 `Stretched()` 同时给了两件事：线高的**下限**（`ascent/descent` 各取 max ✓ 正确）和一个塞进 `statedLine` 的**上限**。`statedLine` 本来是给"页面写明的 line-height"准备的（`line-height: 1` 时字形溢出线盒而不撑高它），但 `normal` 也被当成数字塞了进去：`pre` 字号 0 → strut 自然高度 0 → `line.Height = min(13.59, 0) = 0`。CSS 2.1 §10.8.1 里 `normal` 说的是"行盒按线上的字形取高"，它只能当下限，不能当上限。改法：`statedLine` 只在 `_fallback?.LineHeight is not null`（页面确实写了行高）时并入 strut 高度；下限那条路径不动。

**读数**：`wpt/css-text/` 92 → **87**、其余三块与三块旁证不变（`css-text-decor` 24、`css-writing-modes` 31、`css-ruby` 27、`css-tables` 28、`gecko/writing-mode` 18、`CSS2` 68）。单测 2211 全绿，硬断言 63 通过 / 1 跳过。四块目标语料合计 231 → **169**。

**顺带**：同一条改动让 `text-autospace-elements-002/004/006` 翻绿 —— 那三对的行里也有 0 字号的自动空格插值，线被压扁后注疏与空格叠在一起。剩下的 `text-autospace-*` 五对要先解决 `Measure` 里跨 item 边界的 autospace 增量不一致（`·` 在 `あ·` 里多 2.5 px、在 `·　` 里不多）。

## 24. 2026-11-03 轮续：认不出词界的文字，也要给一个断处

`line-breaking-023..027` 五对是 `relat="mismatch"`（测试页必须与 `white-space: pre` 的参考页**不同**），断言写得很直白：需要词法/正字法分析的语言，即使 UA 不会正确分词，也必须有一种 fallback 断行，**不允许溢出**。我们五对全给成同一张图：泰、老挝、缅甸、高棉、爪哇的字母在 `ClassOf` 里落到 `Al`，两个字母之间没有断点，整串顶在 6em 的盒子里一行画完 ✓ 与 pre 相同 → mismatch 失败。

规范那一句话在 CSS Text 3 §5.1 末尾（不是 UAX#14 的 LB1）：

> In order to avoid unexpected overflow, if the user agent is unable to perform the requisite lexical or orthographic analysis for line breaking any content language that requires it—for example due to lacking a dictionary for certain languages—it must assume a soft wrap opportunity between pairs of typographic letter units in that writing system.

即：**没有词典就得按“任意两个字母之间”放行**，`word-break: normal` 下如此；`keep-all` 仍然把字母扣在一起（§5.4 的 `HoldsTogether` 认 `Al/Nu/Id`，不动）✓ `break-all`/`anywhere` 本来就断 ✓。

实现两行：`TextChunker.RequiresAnalysis(codePoint)`（UAX#14 SA 的那些块：Thai/Lao、Myanmar、Khmer、Tai Le/New Tai Lue/Khmer 符号、Buginese/Tai Tham、Balinese/Sundanese、Batak、Kayah Li/Rejang/Javanese、Shan/Tai Nüa/Tai Viet、Cham），`LineBreaks.ClassOf` 在 `word is WordBreaking.Normal` 时把它们读成 `Id`。附注两点：① 这些块里的元号/上下标符号多是 `Mn`/`Mc`，`ClassOf` 先答 `Cm` → LB9/LB10 把它们粘在前一个字母上，簇不会被拆开 ✓；② 分块器与断行器都走 `ClassOf`，所以一处改完，chunking、`min-content`、行内断点一起对上。

**代价**：`word-break-auto-phrase-002` 由绿转红 —— 那对拿 `word-break: keep-all` + `<wbr>` 当参考，比较的是 `min-content` 宽度；有了 fallback，我们的 `normal` 泰文 min-content 掉到一个字母，而 Chrome 有泰语词典，min-content 是一个词。同族另外四对（001/003/004/005、intrinsic-001）本来就红。净收益 +4。真要清这一族，得给这些文字做词界识别（词典或统计），不是断点规则能补的。

**读数（本轮终点）**：`wpt/css-text/` 95 → **83**、`wpt/css-text-decor/` 24、`wpt/css-writing-modes/` 31、`wpt/css-ruby/` 27；旁证 `wpt/css-tables/` 28、`gecko/writing-mode` 18、`wpt/CSS2/` 68 —— 全不回退。`Hutu.Tests` **2212** 全绿（本轮 6 条新守卫），硬断言 63 通过 / 1 跳过。四块目标语料合计 231 → **165**。

**下一刀（css-text 83 的分布）**：`word-break` 15（含 auto-phrase 5）、`text-fit` 10（属性未实现）、`overflow-wrap` 8、`white-space` 15、`text-autospace` 5、`text-justify` 5、`line-break-anywhere-overrides` 4、`letter-spacing` 4、`text-align`/`text-indent` 各 3、`bidi`/`text-transform` 各 2。三条已经定位、不需要词典的：`text-indent-each-line-hanging`（`each-line` + `hanging` 的首行缩进）、`letter-spacing-ligatures-003`（字距按字形还是按字符）、`empty-span-001`（空 inline 元素在行内的落位）。

## 25. 2026-11-03 轮续：首行缩进的两个修饰符，以及"第一个可格式化行"到底是谁

`text-indent-each-line-hanging`（8800 px）、`text-indent-abspos-hanging-001`（204）、`text-indent-length-002`（800）三对都是整条声明被丢掉：AngleSharp 的文法只到长度为止，`4em hanging` 里的词它不认识，于是连 `4em` 一起不认。先把声明留住 —— `KeywordExtraSubstrings` 加 `["text-indent"] = ["hanging", "each-line"]`，值里出现这两个词之一就整条留原文，其余写法（`40px`、`50%`、`calc(...)`）照旧走 AngleSharp 的类型值，`ResolveTextIndentFused` 只在留原文那条路上 `LengthReader.FromText` 取距离。

语义（CSS Text 3 §5.2，逐条对着 `text-indent-each-line-hanging-ref.html` 的 8 个盒子验过）：记 **被点名的行** = 块自己的首行 ∪（有 `each-line` 时）每个**强制**断行之后的首行。没有 `hanging` → 缩进落在被点名的行上；有 `hanging` → 落在这之外的每一行。实现成 `InlineLayout.TextIndentation(Distance, Hanging, EachLine)`，一个 `Carries(first, afterBreak)`；它跟着缩进一起穿过 `Plan`/`BuildLines`/`Balanced`/`LinePlanKey`（计划缓存的键本来就有缩进，标志不进键就会串色），`BuildLines` 里原来写死的 `Inset(indent)` / `Inset(0)` 换成 `Settled(first, afterBreak)`，`NextLine(forced:)` 已经有强制断行的信号，不必新记。内在尺寸那一趟也按段种子：段首行的缩进 `headIndent`，强制断行后那段 `breakIndent`（无标志时两者就是原来的 `indent`/`0`，`text-indent-min-max-content-001` 不动）。

`text-indent-length-002` 考的是另一半：块里夹了块级子元素，行内文字被切成若干匿名块，**只有开头那一个**是"这个块的首行"，后面每一段都在普通行的起点起行。规范那句 "a line is not available when it does not begin the block container" 的具体形状，参考页写成 ` X`/`X`/` X`/`X`/` X` 五行，非常干脆。做法：`Box.StartsParentContent`（默认 true），`GroupChildren` 造第二个及以后的匿名包裹时置 false（父子里已有在流内的块级孩子）；`Indentation()` 见 `IsAnonymous && !StartsParentContent` 就返回 0。一处收紧：flex/grid 的匿名子项**不是**匿名块盒子，`anonymous-flex-item-001`/`anonymous-grid-item-001` 要求容器里第三个孩子照样缩进（CSS Flexbox 1 §4.1），所以 `parent.Kind` 是 Flex/Grid/Masonry 时不收紧 —— 这两对本轮开头因为我的收紧红过，已修回。

**顺手清掉两处上一轮记录说"已回退"、其实还留在树里的实验**：`steps` 的闸门里加 `trailed`、以及逐步 `trails`（`asks` 的 else 支 `advance + trails`）。两处退回 HEAD 的写法后 `TailorableBreakTests.ABreakAllLeavesTheSpaceToTheWordItCameFrom` 恢复通过（break-all 下那个尾随空格仍跟着词溢出）；对语料读数无差别（`indent2` 的 css-text 与退回前后一致）。`trailed` 上为 `line-break: anywhere` 加的那个条件也试过：整块 css-text 一对不差，按 HEAD 收回，不留没证据的死重。

**读数**：`wpt/css-text/` 82 → **79**（正是上面三对）、旁证 `wpt/CSS2/` 68 → **67**（`block-in-inline-first-line-002`，同一条匿名块规则带出来的）、`wpt/css-text-decor/` 24、`wpt/css-writing-modes/` 31、`wpt/css-ruby/` 27、`wpt/css-tables/` 28、`gecko/writing-mode` 18 全不动。四块目标语料合计 164 → **161**。`Hutu.Tests` **2217** 全绿（本轮 4 条守卫：hanging、each-line、两者同用、匿名段不缩进），硬断言 63 通过 / 1 跳过。

**已知边界**：带 modifier 的声明只认简单长度（`FromText`），`text-indent: calc(1em + 2px) hanging` 这种组合语料里没有，真写了会退化成 0 距离 + 标志；百分比照旧在布局里按容器 content box 解析。多栏（`slotExtents`）的后栏行按 `Settled(first: false, afterBreak: false)` 走，`hanging` 时会给它们缩进，Chrome 怎么算没测。

**下一刀（css-text 79 的分布）**：`word-break` 15（含 auto-phrase 5，需要词界）、`white-space` 15、`text-fit` 10（属性未实现）、`overflow-wrap` 8、`text-autospace` 5、`text-justify` 5、`letter-spacing` 4、`line-break` 4、`hyphens` 3、`text-align` 3、`bidi` 2、`text-transform` 2。

## 26. 2026-11-03 轮续：min/max 也要按字形折，正交流没有块尺寸时给一个 fallback

`available-size-011/017/018` 三对看着是三件事，其实是两条规则，而且第一条不是 writing-mode 的事。

**`ch` 在 clamp 上退化成半个 em。** `height: 4ch` 走的是带字形的读法（我们量 `"0"` 的推进宽度），但 `min-height`/`max-height`/`min-width`/`max-width` 在 fused 路径里传的是 `size` 这个光秃秃的数字而不是 `face` —— 于是 `FaceMeasure` 直接返回 null，落到 `LengthReader.cs:365` 的 0.5em 兜底。`available-size-017` 的父盒是 `height: 4ch; min-height: 8ch`，规范期望 8×11.99 = 95.94，我们给 80，正交子盒就在错的位置上换行。改一处：四个 clamp 跟两个 size 一样传 `face`。这类页的构造很刁：`ch` 在这族测试里同时当"可用高度"和"字符宽度"用，所以只要两边不一致就必然错位 —— 字体换成什么都不知道，测试照样自洽。

**WM3 §7.3.1 的 fallback 尺寸。** 011/018 的父盒是 auto 高度（018 还叠了 `min-height` > `max-height`），块轴没有确定尺寸，正交子盒的 inline 就没得破。规范给的兜底是三者取最小：① 父盒自己的 inner max size（被 inner min size 抬底）、② 最近的祖先 scrollport 的 inner size、③ 初始包含块。我们原来只有 ②③（`Measure` 里 auto 高度一律 `childHeight = 0` 往下传）。补 `BlockLayout.OrthogonalExtent(style, basis, insets, scrollExtent)`，在测量与摆放两处都按 §7.3.1 取 ①并夹在 scrollport 之内；`min > max` 时 min 赢（CSS 2.1 §10.4.3，018 正是这一条）。

顺带把一条自己写错的守卫纠正了：我一开始拿 8 个不可断的 `a` 去测，期望两栏 ——但那是**正确的溢出**。WM3 §7.3.2 说正交根盒的 inline 自动尺寸是 fit-content(min(max-content, max(min-content, stretch-fit)))，一个整词不可断，min-content 就是整个词，所以盒子按内容长到 47.97 而不是被压到 20。守卫改成有空格的 `a a a a a a a a`（15 个单位、20px 可容 4 个）→ 四栏 ✓ 与 011 的形状一致。写守卫前应该先确认内容的断点，而不是先看盒子尺寸。

**读数**：`wpt/css-writing-modes/` 31 → **28**（正是这三对）、`wpt/CSS2/` 67、`wpt/css-text/` 79、`wpt/css-text-decor/` 24、`wpt/css-ruby/` 27、`wpt/css-tables/` 28、`gecko/writing-mode` 18 —— 其余六块逐条比对无变化。四块目标语料合计 161 → **158**（本轮起点 164）。`Hutu.Tests` **2219** 全绿（新增 2 条守卫：`ChIsTheFaceAdvanceOnTheClampsAsWellAsTheSizes`、`AnOrthogonalChildBreaksAgainstTheContainersMaxHeight`），硬断言 63 通过 / 1 跳过。

**下一刀（writing-modes 28 的分布）**：`abs-pos-border-offset-001..003`（3 对）、`tcy-white-space-processing-001..003`（3 对）、`text-combine-upright-*`（4 对）、`table-cell-valign-001..004`（4 对 —— 已查证是 fuzzy `totalPixels=0-1` 与我们的 AA/子像素覆盖对不上，不是能靠规则清掉的）、`ch-units-vrl-003/004`（2 对，同族但成因不是 clamp）、`available-size` 还剩 002/004 等。css-text 79 里最大的仍是 `text-fit`（10 对，属性未实现，规范已从 drafts.csswg.org/css-text-5 取回：`[none|grow|shrink] [consistent|per-line|per-line-all]? <percentage>?`，按行缩放字号而不是字距）。

## 27. 2026-11-03 轮续：下一刀的依据先记下来（text-fit / tcy / ruby 里的制表符）

本轮把三个待做的簇查清了，代码未动，先把依据钉在这里，免得下次重新推。

**`text-fit`（css-text 10 对，属性完全未实现）**。规范在 drafts.csswg.org/css-text-5（全文已存 build/tmp/textfit.txt）。文法 `[none|grow|shrink] [consistent|per-line|per-line-all]? <percentage>?`，继承，作用于块容器，初始 `none`。缩放因子按行算：`factor = (行盒可用宽度 - 不可缩部分) / 可缩部分`，即规范说的 `(A+B)/A`。**可缩部分** = 文本（含 inline 盒里的文本，但不含行尾空白）+ 与字号成比例的间距（百分比的 letter/word-spacing、text-autospace）；**不可缩** = text-indent、line-padding、非百分比的 letter/word-spacing、原子 inline、inline 盒的 inline 轴 padding/border/margin。`consistent`（默认）= 所有行取最小的那个因子；`per-line` = 各行自取，但块末行与以强制断行收尾的行不缩；`per-line-all` = 都缩。`grow` 只放大（因子 >1 才生效），`shrink` 只缩小（因子 <1 才生效）；`<percentage>` 是限幅：grow 且 ≥100% 时为上限，shrink 且在 0..100% 内时为下限。缩放只改**用值字号**：`line-height: 1.5em`、非百分比字距等不跟着变，但 `line-height: normal` 的行盒会随字形高度变。有 float / initial-letter 让行盒宽度随块轴位置变化时整个块不缩。应用在换行之后、两端对齐之前。已逐格核对过的期望值：`grow-consistent`（120px，pre，两行 60/30 → 因子 2 与 4 → 取 2 → 整块 20px）；`shrink-consistent`（80px，160/80 → 0.5 与 1 → 0.5 → 10px）；`grow-per-line`（20px 字号，行1 "ABCD"=80 → 1.5 → 30px，行2 是末行不缩 → 20px）；`grow-per-line-all-line-height` 的 `AB<span border:1px>CD</span>E` 在 102px 里：可缩 50、不可缩 2 → (102-2)/50 = 2；末行规则、固定行高那一版则两行分别是 98/20=4.9 与 …（照参考页写死）；`spacing.html` 的 `letter-spacing:50%` 随字号缩、`letter-spacing:10px` 不缩，最后一格 `shrink per-line-all 75%` 是因子被下限 0.75 抬起（(40-30)/80=0.125 → 0.75 → 15px）；`text-align.html` 与 `text-box.html`、`writing-mode.html` 各用 consistent。

**`tcy-white-space-processing-001..003`（writing-modes 3 对）**。规则：`text-combine-upright: all` 的盒子里，`white-space: normal` 的空格要**两头裁掉、中间折叠**，并且整个组合文本挤进**一个 em** 的行内格。我们现在是每个子 run 各自成段：`<span>"  12"</span>` 画成 `" "`(4px) + `"12"`(16px) 共 20px 深，参考页是 16px；`<span>"5    6"</span>` 我们 16+4+16=36，参考页是一格里放 `5 6`。所以不是只裁空格就完事，组合内容要当一个整体收进 1em（`TextSelector.ColumnStep` 已经按 `Font.Size / run.Text.Length` 铺一格，缺的是"整段合并 + 空白处理"）。

**`ruby-tab-in-base-001..003`（css-ruby 3 对）**。已定位：普通流里 `X<tab>Y`（tab-size:100px，Ahem 25px）会被在制表符处拆段 —— `"X "` 画到 100、`"Y"` 从 100 起 ✓；而 `X<ruby><tab>Y<rt>z</rt></ruby>` 里基准文本 `"\tY"` 是**一个** 100px 宽的碎片，但里面的字形按字体的制表符推进画，`"Y"` 落在 68.75 而不是 100。注 `"z"` 的位置（68.75..81.25，居中在 100px 的格上）与 Chrome 一致 ✓，所以只需让含 `制表符` 的 ruby 基准碎片像普通流那样拆开（或让画制表符时用行内位置推到 stop），pair 宽度与注的居中都不用动。

## 28. 2026-11-03 轮续：ruby 基准丢了 inline 盒的结构；正交根的块轴尺寸被父盒顶宽

读数不动（四块 158），这轮是查证，代码只读未改。两处已经用探针钉死，下一轮可以直接动手。

**`ruby-tab-in-base-001..003`（css-ruby 3 对）**。`InlineLayout.AddRuby`（src/Hutu/Rendering/Layout/InlineLayout.cs:2782）把基准子节点 `Flatten` 成**一个字符串**，于是基准里"没有文字、只有 padding/border 的 inline 盒"贡献 0。对照实验（build/tmp/empt.html）：`X<span style='padding-left:75px'></span>Y` 在普通流里画成 X(0..25) + span(25..100) + Y(100..) ✓；同样的 span 放进 `<ruby>` 基准里就整个消失，基准只剩 "Y" 宽 25。参考页 `ruby-tab-in-base-001-ref.html` 正是用 `<span class=tab style="padding-left:75px">` 表达制表位，所以我们连参考页都画错（ref 里 Y 落在 33 而不是 108）。注 `z` 的居中位置本来就和 Chrome 一致，所以不必动 pair 宽度。要修的是让 ruby 基准带**分段结构**（文本段 + 各 inline 盒的 inline 边缘），这是一处真重构，顺带可能清掉 `empty-ruby-text-container-abs` 一族。

**`two-levels-of-orthogonal-flows*` + `three-levels-of-orthogonal-flows`（writing-modes 3 对）**。`div style="writing-mode:vertical-rl"`（auto 宽度）里面再套一个 horizontal-tb div：Chrome 把外层的**块轴**尺寸（这里的物理宽度）按内容的 fit-content 算，参考页就写成 `width:fit-content`；我们顶到了包含块的 784，于是黄色底铺满整行。探针实测：test 外层 `div 8,85.16 784x17.72 vertical`，ref `div 8,85.16 98.75x17.72`（98.75 正是 "A B C D E F G" 一行）。两处要改：`MeasureColumn` 的 `childBlock = declaredBlock ?? context.ContainingBlock.Width`（src/Hutu/Rendering/Layout/BlockLayout.cs:220）与 `Layout` 里竖分支的 `?? context.ContainingBlock.Width`（同文件 648-649）——摆放那一处应当用本盒已定的 `contentWidth`，测量那一处需要**先按块轴开放地量一遍子节点**（拿内在尺寸）再夹在可用宽度内，两趟必须给出同一个数，否则又会踩"同一个孩子被按两种约束量两次"的坑。`-percentage` 那一版只差 144 像素：内层 `width:50%` 我们按 body 的 784 折成 392，Chrome 按 §7.3.1 的 fallback（此处就是视口 800）折成 400，差的正是 8 列 × 18 行 = 144 ✓ 所以百分比的基底也要走同一条 fallback，不能退回最近一个确定宽度。

**下一轮的次序建议**：先做 `text-fit`（10 对，依据在 §27，改测量→摆放→绘制三层，`consistent` 那半边能一次清 5 对：`grow-consistent`、`shrink-consistent`、`text-align`、`text-box`、`writing-mode`），再回来啃 ruby 基准分段（3 对）与正交根块轴（3 对）。

## 29. 2026-11-03 轮续：`text-fit` 落地，css-text 79 → 74

属性按 CSS Text 5 §1.1 实现（规范全文与十个参考页的算法都在 §27，本轮照它做）。文法 `[none|grow|shrink] [consistent|per-line|per-line-all]? <percentage>?`，继承，作用于块容器。AngleSharp 对它一个字都没有，所以进 `RawValueNames` 留原文、并进 `InheritedRawNames` 让它随层叠往下走；`ComputedStyle.TryReadTextFit` 逐 token 认：认不出的 token 当整条无效（回到继承值），没有方向词就是 `none`，`75%` 存成 0.75。存进 `TextArea.Fit`，`text-fit` 同时加进 `TextNames`，"这个元素一个字没写就整块继承父面积"的捷径照旧。

布局在 `InlineLayout.FitLines`，挂在 `Plan` 里换行与行夹裁之后、两端对齐之前（§1.1 附录 A）。因子是 `room / scalable`，而 **`room = line.Extent - line.Indent - (line.Used - scalable)`**：不可缩部分不逐项去数，而是取"这一行已经用掉的宽度里，文本之外的那一截"，一次覆盖原子盒与替换内容、inline 盒沿行轴的 padding/border/margin、以及 `pre` 行尾仍占位的空格（§1.1.1）。`scalable` 只累加文本碎片的宽度。方向决定够不够格缩：`grow` 取 `max(1, factor)`，`shrink` 取 `min(1, factor)`；`<percentage>` 经 `TextFit.Clamp` 生效 —— grow 且 ≥100% 是上限，shrink 且 ≤100% 是下限，反过来写就不限。缩放范围：`consistent`（默认）整块用各行因子的**最小值**；`per-line` 每行自取，但块末行与**以强制断行收尾的行**不缩；`per-line-all` 都缩。有 float 让行盒宽度随块轴位置变化时整块不缩（规范明说）。

落地：`ScaleLine` 把可缩碎片宽度乘因子，`line.Used` 只加文本变化那一段（不可缩的部分保持原位）；`line.Ascent`/`line.Height` **只在块没写 `line-height` 时**（`container.Style.LineHeight is null`，即 `normal`）才乘因子 —— 写下的行高是计算字号的长度，不随缩放变（§1.1）。绘制与基线走 `FittedFont(line, style)`：同族同权同斜，只把尺寸乘 `line.Scale`，computed font-size 不动，所以 line-height、非百分比字距都照原样读。

**结果**：`grow-consistent`、`shrink-consistent`、`grow-per-line`、`shrink-per-line-all`、`writing-mode` 五对转绿；`wpt/css-text/` 79 → **74**，其余六块（writing-modes 28、tables 28、CSS2 67、gecko 18、decor 24、ruby 27）逐条 diff 不变。四块目标语料 158 → **153**。守卫 7 条在 src/Hutu.Tests/TextFitTests.cs，`Hutu.Tests` **2226** 全绿。

**两处是我把期望算错，不是引擎错**，记下来免得再犯：① `white-space:pre` 的两行 "4 字 / 10 字" 因子是 5 与 **1**（10 × 5px 正好铺满 100px），不是 5 与 2；② `per-line` 说的 "lines that end in a forced break" 指的是**前一行**以强制断行收尾，所以 pre 的两行（末行 + 断行收尾）全都不缩。写完守卫先怀疑自己读的规范，再怀疑代码。

**还红 5 对**：`grow-per-line-all`（1900 px）与 `grow-per-line-all-line-height`（536 px）—— 靠 Used 推不可缩部分已经接近但不齐，要再看 inline 边缘与行高的相互作用；`spacing.html`（34034）需要把 letter/word-spacing 按**百分比 vs 长度**拆开（百分比随字号缩，长度不缩，现在一律算在文本里）；`text-align.html`（2534）与 `text-box.html`（19820）分别要查对齐和 `text-box` trim 与缩放的相互影响。

## 30. 2026-11-03 轮续：text-fit 剩五对的成因定位（代码未动）

读数仍是 fit3 那一组（css-text 74 / decor 24 / writing-modes 28 / ruby 27 = 153；CSS2 67、tables 28、gecko 18），`Hutu.Tests` 2226 全绿、Conformance 63/1。本轮是查证，两处钉死：

**inline 盒的边框带没有跟着缩放**（`grow-per-line-all` 1900px → 本轮 Used 推导后 `grow-per-line-all-line-height` 412px，形状完全一样）。扫描两张图：lime 边框的 x 范围两边都是 48..89 ✓ 宽度对（40px 的 "CD" + 2px 边），差的是竖直范围 —— 参考页 y=7..28（高 22），我们 y=15..26（高 12 = 10px 字号的带 + 2px 边）。也就是说碎片宽度已经按因子放开了，**inline 祖先盒的带（border-box 的顶与高）还是按未缩放字体算的**。要改的不是 `InlineLayout.cs:2378` 那处（那是 ruby 基准专用的 band，已经吃 `FittedFont`），而是 `Advance`/`CloseBox` 一族：祖先带的高度来自 `OpenInline` 进来时用 `item.Style.GetFont()` 量的那份度量（对照 4362/4376/4421 `room.Grow(LineBoxHeight(item.Style, item.Style.GetFont()))` 这些建线时的调用 —— 它们跑在 `FitLines` 之前，所以线高我能事后乘因子，而祖先带的顶/高是在摆放期由碎片自己的度量再算一次，没走 `FittedFont`）。修法：摆放期凡是给 inline 祖先算带的地方，都改读 `FittedFont(line, style)`；`line.Scale` 已经带在线上了。

**百分比字距要在绘制里也缩**（`spacing.html` 34034px）。几何侧按 Used 推不可缩部分已经对：`letter-spacing:10px` 那两格（box 2、4）用 `A = 文本本体`、`fixed = Used - A` 折出 2.5 / 2 的正确因子；但 `letter-spacing:50%` 与 `word-spacing:50%` 是可缩部分（§1.1.1：随字号成比例的间距要一起缩），参考页把 50% 折成缩放后的 20px/20px。**绘制侧读的是 `Style.LetterSpacing` 这个已经按 computed 字号折好的数字**（`InlineLayout.cs:2568`、`7874`、`TextSelector.cs:558`、`LayoutEngine.cs:1121`），所以字距不会随 `line.Scale` 变。修法：只有当该间距是**百分比声明**（`ResolvedFont.LetterSpacingPercent` / `WordSpacingPercent` 非 null）时，把绘制/量字用的间距乘 `line.Scale`；写成 px/em 的照旧不乘。`spacing.html` 最后一格还顺带验 `TextFit.Clamp` 的下限方向（已经对：守卫 `APercentageLimitsHowFarAShrinkFitMayGo` 绿）。

**另外两对**：`text-align.html`（2534px，first at 10,9 test #0000ff ref #0000d3）与 `text-box.html`（19820px，first at 10,4 test 黑 ref 白）。前者像是对齐用的 extent 与缩放后的 `Used`/`Hanging` 相互作用（两端对齐前缩放，`AlignLastLines`/`DistributeSpace` 会把 `NaturalUsed` 再吃一遍），后者要查 `text-box-trim` 取的行高与缩放后行高的先后。两对都在同一台机器上一测就知道，别再猜。

## 31. 2026-11-03 轮续：inline 盒的带随 text-fit 缩放（两对逼近，未转绿）

`CloseBox`（src/Hutu/Rendering/Layout/InlineLayout.cs:2197 一带）以前用 `ancestor.Ascent`/`ancestor.Descent` 直接算元素画出的带 —— 那是建 `InlineAncestor` 时用 `box.Style.GetFont()` 量的，`InlineAncestor` 在 Collect 期生成、跨行共享，所以拿不到行因子。改成在用它的时候乘 `line.Scale`（`ascent`/`descent` 两个局部量），padding/border/margin 仍是页写的长度，不乘。`line.Scale` 默认 1 ⇒ 没写 `text-fit` 的页面逐位不变，所以其余六块不必重跑（构造上无影响）。

读数：`wpt/css-text/` 仍 **74**（没有新转绿），但两对大幅逼近 —— `grow-per-line-all-line-height` 536 → **312** px、`grow-per-line-all` 1900 → **1800** px（lime 边框那条 42×22 的竖直带已经对齐）。`Hutu.Tests` **2226** 全绿，CSS2 67 / tables 28 / gecko 18 / decor 24 / writing-modes 28 / ruby 27 未重跑（按上文理由不变）。四块目标语料合计仍 **153**。

**还差的三件事**，都在 §30 里定位过、这次没来得及做：
1. `grow-per-line-all-line-height` 的 312 px 现在落在 `border:1px solid green` 那一格（box 2，`AB\nCDEFG` 用 per-line-all 分别缩 4.9 与 2），`first at 8,73 test #000000 ref #008000` —— 是绿边框的位置/高度差，不是文字本身；下一轮先看这一格的两次缩放有没有把 `line.Ascent`（写了 `line-height` 时不乘因子）与已乘的 `ascent` 混用。
2. `spacing.html` 34034 px：百分比 letter/word-spacing 要在绘制与逐字定位里也乘 `line.Scale`（`InlineLayout.cs:2568`、`:7874`、`TextSelector.cs:558`、`LayoutEngine.cs:1121` 都读 `Style.LetterSpacing` 这个已按 computed 字号折好的数字）；判据是 `ResolvedFont.LetterSpacingPercent` / `WordSpacingPercent` 非 null 才乘。同时 `ScalableExtent`/`ScaleLine` 要把 px 字距算作不可缩（`FixedSpacing(fragment)`），否则 `letter-spacing:10px` 那两格的宽度会被多缩。
3. `text-align.html`(2534) 与 `text-box.html`(19820)：前者怀疑是两端对齐/`AlignLastLines` 重吃 `NaturalUsed`，后者是 `text-box-trim` 取的行高与缩放后行高的先后。两对都在同机一测便知，别再推。

## 32. 2026-11-03 轮续：inline 带的缩放留下，两条字距实验回退

**留下**：CloseBox 里元素画出的带（ncestor.Ascent/Descent）乘 line.Scale。实测 grow-per-line-all-line-height 536 → **312** px、grow-per-line-all 1900 → **1800** px，wpt/css-text/ 仍 74（这两对差最后一格的溢出墨，见下）。Hutu.Tests **2226** 全绿。

**两条已试、量出来更差、已回退**（别照原样再试）：
1. 把 §1.1.1 的"非百分比字距不可缩"实现成 FixedSpacing(fragment)（按 ResolvedFont.LetterSpacingPercent/WordSpacingPercent 判百分比），从 ScalableExtent 里扣掉、ScaleLine 只乘文本本体 —— `spacing.html` 34034 → **34310**。规范是对的，但我们的 line.Used 与碎片宽度对不上这个拆分（Used 里字距到底算进哪一边没查清），推翻了 oom = Extent - Indent - (Used - scalable) 的一致性。
2. 再把百分比字距在绘制里也乘缩放（LayoutBox.FitScale + Painter.DrawnLetterSpacing/DrawnWordSpacing）—— 34310 → **35070**，更差。说明绘制侧本来就在某处跟着字号走了（或参数位置被我当成 letterSpacing 的那个其实另有含义）。

**下一刀的正确顺序**：别再从"字距算哪边"下手。先把 `spacing.html` 第一格（`width:100px; letter-spacing:50%`，A<span class=nospacing>A</span>）的**布局树**与**绘制出的逐字推进**各打一遍，看 100px 里两枚 A 到底落在哪：现在 irst at 8,0 test #008000 ref #000000 说明差异从第一行第一列就开始，先确认是谁在错，再决定改几何还是改绘制。另外两对（	ext-align 2534、	ext-box 19820）同理，都是一测便知，别再推。

## 33. 2026-11-03 轮续：行高要分"倍数"和"长度"，text-box 转绿

上一轮定下的规矩是"先测别再推"，一测就中。把 `spacing.html` 第一格单独拿出来打布局树：test 与 ref 的**文字水平位置完全一致**（两块都在 x=0..60、60..100，字体 40@Ahem），差的是竖直 —— test `fill 0,0 100x20 / text base 16`，ref `fill 0,0 100x40 / text base 32`。也就是说行的**块尺寸**没跟着缩放：参考页写的是 `font-size:40px` 配继承下来的 `line-height:1`，倍数是拿"真正画出来的字号"乘的，于是行盒 40；我们的 20 是没乘因子的那一个。

规范那句话读窄了：`This property does not affect the font-size computed value, and thus does not affect font-size-relative <length> values of other properties. For example "line-height: 1.5em" and "letter-spacing: 0.1em" are not affected` —— 说的是**长度**（em/px）不跟着动；而 `line-height: <无单位数值>` 和百分比本来就不是长度，是"按所用字号重算的倍数"，要跟着缩放。于是加 `TextArea.LineHeightTracksFont`：`ComputedStyle.LineHeightFollowsFont(text)` 认"空 / `normal` / 以 `%` 结尾 / 能当纯数解析"四种为跟随，其余（带单位的长度）不跟随；两条路径都填 —— 融合路径取 `find("line-height") ?? parent.LineHeightHit` 的原文（无单位数值是往下重乘的，所以要带声明而不是父值），旧路径取 `ReadKeyword("line-height")`。`ScaleLine` 由"块没写行高才乘因子"改成按这个判断。

顺手把上一轮量差的两条里**该留的那半**留下：`LayoutBox.FitScale`（摆放时等于 `line.Scale`）+ `Painter.DrawnLetterSpacing/DrawnWordSpacing` —— 只有当声明是**百分比**字距时才乘 `FitScale`，写成 px/em 的不乘（§1.1.1 的"随字号成比例的间距可缩"）。上一次它让 `spacing.html` 变差，是因为竖直那半还没修；这次 19832 → **18702**，方向对了。`GatherTextRuns`（光标/命中表）也一并走同一 helper，免得天真点错位置。

**读数**：`wpt/css-text/` 74 → **73**（`text-fit/text-box.html` 转绿），其余六块 fit9 与 fit7 一致：writing-modes 28、tables 28、CSS2 67、gecko 18、decor 24、ruby 27。四块目标语料 153 → **152**。守卫新增一条 `ALineHeightWrittenAsAMultipleGrowsWithTheFitWhileALengthStands`（`line-height:1` 缩到 20、`line-height:10px` 保持 10），`Hutu.Tests` 全绿。

**text-fit 还红 4 对**：`spacing` 18702（first at 8,57 已经是浅灰，说明前面几格只剩 `:first-line` 灰带与后段字距）、`grow-per-line-all` 1800、`text-align` 2534（test #0000ff / ref #0000d3，看着像缩放后 `Used`/`Extent` 的小数与对齐再分配打架）、`grow-per-line-all-line-height` 312（一格溢出墨的竖直范围）。都还是"先打树再改"的路子。
