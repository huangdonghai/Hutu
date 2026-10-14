# CSS 2.1 正确性推进记录

这篇文档记录 `conformance/reftests/**` 里 **CSS 2.1 基础特性** 的修复进展、优先级判断、环境前提和已知陷阱。
目的：这些结论原先只散在会话上下文里，落一份档以便续接、避免重复侦察。评分机制本身见 [`reftest-scoring_zh_CN.md`](./reftest-scoring_zh_CN.md)。

---

## 1. 当前基线（空载全量，"画对(pair declares)"计数）

| 语料 | 基线 | 备注 |
|---|---:|---|
| **wpt** | **2872** | 含 Ahem 解锁 +95、定宽 BFC 下移 +2 |
| **gecko** | **1850** | 含定高 margin 修复 +19 |
| 崩溃 / 画不出 | **0 / 0** | |
| 单测 | **555** | |

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

---

## 4. 工作流（每个特性族一轮）

1. 从 `build/reftest-score.md` 按特性族聚类（路径 2/3 段 + 关键字），按"单一根因 × 用例数 × 同质性"选目标。
2. 读代表例的 test/ref，精确反推规则。
3. `HUTU_SCORE_LOOK=<子串>` 驱动 `LooksAtOnePairClosely` 做单对 deep-look：输出两侧布局树 + diff map，拿到**确切几何**再动手。
4. 改代码 → 加/改单测（`src/Hutu.Tests`）→ `dotnet test src/Hutu.Tests`。
5. **空载全量无回归门禁**：目标族净正、其它族不回退、0 崩溃，才允许提交。
6. 语义化 `fix(css):` / `feat(css):` 单独 commit。**永不主动 push**（远端推送由用户触发）。
7. 门禁不过（净负或平局）→ `git checkout` 回退到干净基线，WIP 存 stash 备查，如实汇报。

---

## 5. CSS 2.1 族菜单与可胜性（post-Ahem）

### 5.1 推荐先打（纯几何、同质、CSS 2.1 核心）
- **`wpt/css-lists`（110 例，仅 ~172K px ≈ 平均 1.5K/例）**：全是**近失**，marker 定位用 Ahem 方格后应大面积翻绿；列表标记是 CSS 2.1 核心。最像"一处根因翻一片"的大族。**（下一个首选目标）**
- **`wpt/CSS2/normal-flow`（16）+ `CSS2/tables`（9）+ `CSS2/visuren`（9）**：纯几何、最基础，很多已因字体对等而逼近。

### 5.2 margin-collapsing 残余（58 例，已不同质，偏深）
- **负边距 self-collapse**（`block-no-content-2e/3b`）：相邻 run 正负边距折叠算术，逐例不同。
- **测量感知的 min/max-height**（`{min,max}-height-separates-margin`、`margin-collapse-min-height-*`、`block-zero-min-height`）：规则成立**取决于 used height 是否真的钳住内容**，而 `CollapsedEnd` 是纯样式静态判定——要做成测量感知，比定高那条更侵入。
- **`block-xhtml-root` / `table-caption`**：耦合 DOM 解析 / 表格子系统。

### 5.3 暂缓 / 不可胜（明确排除）
- **`white-space` 残余多是 `break-spaces` / `pre-wrap`（CSS Text 3，非 2.1）**——按"先弄好基础"的口径暂缓。
- **`floats` / `floats-clear`（CSS2 里约 65 例）**：异构多根因，非一处能翻一片。已收（`ab2def6d`）：**定宽 BFC 盒装不下时下移**（+2）。剩余子因待攻：**`floats-placement-*` 疑似浮动背景未绘制 / paint 层序 bug**（float 布局矩形对却画成容器底色——若是 paint 问题影响面大、优先查）；**`new-fc-separates-from-float` 的 BFC↔浮动 margin 分离**；**`*-clearance` / `clear-*` 的 clearance+顶边距算术**；**`floats-clear-multicol-*`（耦合 multicol）**。早期"仅 BFC 容器才包含浮动"的核心不变量修复实测净 −14（谓词漏 flex/grid item + 需跨块 band 折行），已回退。
- **grid-lanes / masonry（先进排版）**：用户已指示"先进排版先不弄"，M2 最短车道 WIP 存于 stash，M4/M5/M6 未做。

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

- [ ] `wpt/css-lists` marker 定位族：deep-look 代表例 → 定位根因 → 修复 → 全量门禁 → commit。
- [ ] `wpt/CSS2/normal-flow` 纯几何批次。
- [ ] （较后）margin-collapsing 负边距 self-collapse 与测量感知 min/max-height 族。
