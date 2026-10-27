# CSS 2.1 正确性推进记录

这篇文档记录 `conformance/reftests/**` 里 **CSS 2.1 基础特性** 的修复进展、优先级判断、环境前提和已知陷阱。
目的：这些结论原先只散在会话上下文里，落一份档以便续接、避免重复侦察。评分机制本身见 [`reftest-scoring_zh_CN.md`](./reftest-scoring_zh_CN.md)。

---

## 1. 当前基线（空载全量，"画对(pair declares)"计数）

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

- [ ] **`text-emphasis-position` 的 8 对**（`005d/e/f/g`、`006d/e/f/g`，签名同为 `739 像素 / 首差 51,50`；005 系是"该有号处画白"、006 系是"该白处画了号"，两两镜像）。矩阵已核实：16 个用例全是 `text-orientation: sideways`，`vertical-rl` 那 8 个全通过、`vertical-lr` 这 8 个全失败，且 `over/under` 与 `left/right` 的四种写法结果一致。
  - **曾猜"是 Side 在 vertical-lr 下换了线的哪一边"，已被实测否证**：把通过的 `005`（rl）和失败的 `005d`（lr）各跑一次 deep-look，两页的布局树、行盒、以及 5 个强调号的坐标**逐字相同**（都在 x=88、同一串 y）。所以号落在哪一边不是差异所在，`StandsAtTheEnd` 这条路径两处都给出一样的结果。
  - **实测到的唯一差别**：文本 run 的 base 不同 —— rl 是 `base 33.91`、lr 是 `base 41.91`（同一行盒 88x80、同一 font 16）。也就是说 sideways 在 lr 下把基线抬高了 8，接近一个 ascent/descent 的差；应从 `text-orientation: sideways` 与 `vertical-lr` 交汇处的行高/基线算起（CSS Writing Modes 3 §4.2 的 sideways 定义与 §3.2 的行方向），而不是从强调号位置算起。
  - 同族 `001/001a-c`、`002/002a-c` 是另一种征状（pair 声明 mismatch、我们却画成同一张图），不在这条里。
- [ ] **`position-relative-010/013`：单元格的百分比偏移仍按 used 高度求解**（deep-look 已核数字，非推测）：
  - 010 —— 偏移在 `<td style="top:100%">`，表格/`tbody`/`tr` 都没声明高度，规范要求移动 **0**；我们把 td 画在 y=149.72（行 used 高 100，被格内那个 100px 的 div 撑出来的），绿块整个落到红块下缘外，20000 像素差。
  - 013 —— 同样偏移在 `<td>`，父 `tr` 声明了 `height:10px`，规范要求移动 **10**；我们同样移动 100（td 在 149.72，格内 div `top:-10px` 落在 144.72），19000 像素差。
  - 表部分（`thead`/`tbody`/`tfoot`/`tr`）这一半已按**声明高**修好（`7a63e476`）：012 从 19000→1000、011 位移改对（行现走 10 而不是 100），两页各差一条 10 高的横带、暂未翻绿；`008/009` 保持通过，全量 0 回退（`css-ruby/ruby-align-002` 那次是负载抖动，单独跑 2/2 且页内无表格/无 relative）。
  - **落点**：单元格走通用块路径 `LayoutEngine.Arrange`（`src/Hutu/Rendering/Layout/LayoutEngine.cs:1181`），基准是 `context.ContainingBlock`，而格子 CB 在 `TableFormattingContext.ArrangeCell:429` 用 `contentHeight`（行 used 高 − 内缩）塞进去 —— 于是百分比读到 used 值。
  - **两条候选与代价**：(a) 把父部分的声明高送进格子 CB —— 会同时改变格内**子元素**百分比高度的解析基准（`definite` 那套），牵连整族表格页；(b) 在表格侧自算格子偏移、并阻止通用路径二次平移 —— 需要给 cell 的 Box 传一个"偏移已应用"的记号，否则位移两次。两条都要跑 `CSS2/tables`+`css-tables`+`css-position` 三个模块做门禁；建议先 (b)，它不改任何既有百分比语义。
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
