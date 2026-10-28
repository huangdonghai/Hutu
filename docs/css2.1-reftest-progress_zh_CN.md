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
5. **回归门禁（用户明确指示，2026-10 起，覆盖旧的"空载全量"口径）**：只用**固定样本 60 条**——`$env:HUTU_SCORE_LIMIT='60'` 跑 `TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay`（约 6 秒，每个语料各画前 60 页，结果确定），与基线（gecko 54/60、wpt 45/60）比对，不得回退、0 画不出。**不要再跑全量计分**（13930 pair / 27940 html，约半小时，太慢）。目标族净正用 `HUTU_SCORE_LOOK` deep-look 逐对确认。也不要把 `StopsAtTheTenthFinding` 的 findings 当回归基线（队首样本，非确定）。
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
