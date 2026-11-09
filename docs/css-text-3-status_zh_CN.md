# CSS Text Module Level 3 实现盘点

> 审计日期 2026-10-30，引擎 HEAD `7357d65a`。这是一次**只读盘点**：数据来自读代码、读规范与当日全量语料运行，
> 没有为这份文档改任何代码。语料数字取自 `build/_folders2.tsv`，红对名单取自 `build/_folders2-fails.txt`，
> 规范文本取自当日抓取的 <https://drafts.csswg.org/css-text-3/>（`build/_text3.html`）。
> 布局实测取自 `LayoutProbe`（`HUTU_PROBE_PAGE`）导出的 `build/probe-*.txt`。

## 0. 口径与范围

Text 3 ED 自己定义的 propdef 共 **16 个**：

`text-transform`、`white-space`、`tab-size`、`word-break`、`line-break`、`hyphens`、
`overflow-wrap`、`word-wrap`（旧别名）、`text-align`、`text-align-all`、`text-align-last`、
`text-justify`、`word-spacing`、`letter-spacing`、`text-indent`、`hanging-punctuation`。

Text 3 是 Text 4 的子集，但不等同。下面这些**不在本盘点范围**，已在
`docs/css-text-4-status_zh_CN.md` 中盘点：

| 只在 Text 3 | 只在 Text 4 |
|---|---|
| `text-align-all`（Text 4 已删） | `text-align: <string>`、`text-wrap*`、`wrap-inside` / `wrap-before` / `wrap-after` |
| `text-justify: distribute`（旧值） | `text-justify: ruby`、`no-compress` |
| `word-spacing` / `letter-spacing` 只有 `<length>` | 两者都有 `<percentage>` |
| `text-transform` 无 `math-auto` | `hyphenate-character`、`hyphenate-limit-*`、`text-autospace`、`text-spacing-trim`、`text-spacing`、`word-space-transform` |
| §7.3 跨元素边界整形、§8.3 行盒基方向 | （Text 4 引用这些条款，不重复定义） |

因此 Hutu 已实现或未实现的 `hyphenate-character`、`text-autospace`、`text-spacing-trim`、
`word-space-transform` 都不计入本文；Text 3 独有的语义差异在正文中标出。

## 1. 总表

| 状态 | 属性 | 语料 |
|---|---|---|
| 完整 | `tab-size` | 13/13 |
| 完整 | `hanging-punctuation` | 17/17 + 变体 1/1 |
| 完整 | `text-align-last` | 所在文件夹内全部绿 |
| 完整 | `word-spacing`（Text 3 语法） | 2/2（含 1 对 Text 4 百分比，属超集） |
| 别名处理 | `word-wrap` | 与 `overflow-wrap` 同路径 |
| 部分实现 | `white-space` | 233/254（其中 Text 3 内的红 17，Text 4 的 balance/wrap-after 红 4） |
| 部分实现 | `text-transform` | 16/18（5 对假通过；缺 `full-width` / `full-size-kana`） |
| 部分实现 | `word-break` | 47/62（其中 Text 3 内的红 10，Text 4 `auto-phrase` 红 5） |
| 部分实现 | `line-break` | 82/91（`loose` / `strict` 无行为；语料只测 `anywhere`） |
| 部分实现 | `hyphens` | 18/21（`auto` 无词典；2 对假通过） |
| 部分实现 | `overflow-wrap` | 36/44 |
| 部分实现 | `text-align` | 55/58 |
| 部分实现 | `text-justify` | 0/5 |
| 部分实现 | `letter-spacing` | 4/9 + 1 无法绘制 |
| 部分实现 | `text-indent` | 11/14 |
| 未实现 | `text-align-all`（长写本身） | 语料几乎无语义覆盖 |

## 2. 逐项证据

### 2.1 完整实现

- **`tab-size`**：数与长度两种形式都在 `src/Hutu/Rendering/ComputedStyle.cs:2270` 解析。13/13。
- **`hanging-punctuation`**：`none | [ first || [ force-end | allow-end ] || last ]` 全语法在
  `src/Hutu/Rendering/Layout/HangingPunctuation.cs:41`，悬挂在 `InlineLayout.SettleLines` 中按
  start → end 顺序执行。17/17，变体 1/1。Text 3 §8.2.1 与 Text 4 同文。
- **`text-align-last`**：`auto | start | end | left | right | center | justify | match-parent` 全在
  `InlineLayout.AlignLastLines`（`src/Hutu/Rendering/Layout/InlineLayout.cs:5874`），含 `auto`
  对 justify 的降级规则（justify 的末行落到 start）。所在文件夹的直接用例全绿。
- **`word-spacing`**：Text 3 只要求 `<length>`；Hutu 另收百分比（Text 4 的超集），
  见 `src/Hutu/Rendering/ComputedStyle.cs:260`。2/2，其中 `word-spacing-percent-001` 是 Text 4 用例。
- **`word-wrap`**：旧别名按别名读（`src/Hutu/Rendering/ComputedStyle.cs:310`、
  `src/Hutu/Rendering/ComputedStyle.cs:830`），现代名优先。

### 2.2 部分实现

- **`white-space`**：六个整值 `normal / pre / nowrap / pre-wrap / break-spaces / pre-line` 全接
  （`src/Hutu/Rendering/ComputedStyle.cs:4567` 起的四组判定）。21 个红对中 **17 对是 Text 3 行为**：
  `break-spaces` 行首/行尾与表意空格（6 对）、`pre-wrap` 尾随空格与 justify（4 对）、
  `line-edge-white-space-collapse-001/002`、`tab-bidi-001`、`white-space-intrinsic-size-002/004`、
  `textarea-break-spaces-001`；另 4 对属 Text 4（balance 浮动/line-clamp、`wrap-after`）。
- **`text-transform`**：只做 `none / uppercase / lowercase / capitalize`（含希腊 tonos/dialytika 定制），
  见 `src/Hutu/Rendering/Boxes/TextTransformResolver.cs:53`。Text 3 要求的 `full-width`、
  `full-size-kana` 在代码里没有任何引用。红对 `letter-spacing-001`、`upperlower-032`；
  另有 5 对假通过（见 §4）。
- **`word-break`**：Text 3 的 `normal / keep-all / break-all / break-word` 全读
  （`src/Hutu/Rendering/ComputedStyle.cs:4662` 起；`break-word` 按 legacy 别名走 overflow-wrap 路径）。
  Text 3 内的 10 个红对都指向 UAX#14 的 tailor：
  - 东南亚词边界：`normal-002`、`normal-003`、`normal-th-001`、`keep-all-011`（中文逗号）；
  - 埃塞俄比亚习惯规则：`normal-ethiopic`、`break-all-ethiopic`；
  - `break-all-023`（UAX14 PR 类后不得断）、`break-all-inline-008`、`min-content-007`、
    `break-boundary-2-chars-001`（`white-space: pre` 交互）。
- **`line-break`**：只区分 `anywhere`（`src/Hutu/Rendering/ComputedStyle.cs:4627`）；
  `auto / normal / loose / strict` 共用一套 UAX#14 规则，**`loose` / `strict` 没有 tailor**。
  9 个红对：`anywhere-overrides-uax-behavior-009/010/013/014`（GL 类不得被 `anywhere` 覆盖）
  与 `line-breaking-023..027`。**语料只声明过 `anywhere`**——`css-text/line-break` 的 82 个文件里
  119 处声明全部是 `anywhere`，所以 82/91 不能说明 strict/loose。
- **`hyphens`**：`none / manual / auto` 都存下来（`src/Hutu/Rendering/ComputedStyle.cs:843`），
  `none` 丢软连字符（`src/Hutu/Rendering/ComputedStyle.cs:884`），但**自动断词没有词典/算法**，
  `auto` 只是允许软连字符。红对 `hyphens-overflow-001`、`hyphens-vs-float-clearance-002`、
  `hyphenate-character-005`（属性属 Text 4，但失败原因是自动断词）。`hyphens-auto-001/002`
  是假通过（见 §4）。
- **`overflow-wrap`**：`normal / break-word / anywhere` 全读
  （`src/Hutu/Rendering/ComputedStyle.cs:4679`），并区分 anywhere 与 break-word 的 min-content。
  8 个红对：带 `span` 的样式文本（`anywhere-010`、`anywhere-span-001`、`break-word-010`、
  `break-word-span-001`）、inline-block 边界（`anywhere-inline-004`）、字素簇（`cluster-001/002`）、
  tabs 与 min-content（`min-content-size-009`）。
- **`text-align`**：Text 3 语法（无 `<string>`）全接：`start / end / left / right / center / justify /
  match-parent / justify-all`；`match-parent` 在 `src/Hutu/Rendering/ComputedStyle.cs:354`，
  `justify-all` 归一为 justify + justify（`src/Hutu/Rendering/ComputedStyle.cs:359`），
  偏移在 `InlineLayout.AlignmentOffset`（`src/Hutu/Rendering/Layout/InlineLayout.cs:6070`）。
  3 个红对：`start-016`、`end-016`（`pre` + `dir=auto` 首强字符方向）、`justify-bidi-control`。
- **`text-align-all`**：**长写本身没有实现**（全仓 0 处引用）。`text-align` 简写路径覆盖了它的
  日常用法；真正写 `text-align-all:` 的页面会被忽略。语料只有 `text-align-last-wins-001`
  在标题与 help 链接里提到它，并没有声明。
- **`text-justify`**：只认 `auto / inter-word / inter-character / distribute`
  （`src/Hutu/Rendering/Layout/TextClusters.cs:319`），Text 3 的 **`none` 缺失**；0/5。
  实测（Ahem 24px、`text-align-last: justify`，见 `build/probe-justify5.txt`、`probe-justify6.txt`）：
  - `none` + `"A B"` 拉伸到 472（应为自然宽）——`none` 值没有生效；
  - `inter-character` + 拉丁 `"AB"` 保持 48，CJK `"文字"` 拉满 472——**字母间扩张只对 CJK 生效**；
  - `inter-word` + `"文字"` 保持自然宽（正确，不扩 CJK 缝），`auto` / `inter-character` 下拉满；
  - `inter-word-001` 的 probe 显示三行都拉满到 452，与参照的 float 手法几何一致，仍红的原因未定位。
  - 另外 §6.4.5 要求 `auto` 的机会包括“块状文字单元与**任何**单元”的边界；Hutu 的 `auto`
    只认 CJK–CJK，CJK–Latin 边界不算机会。
- **`letter-spacing`**：Text 3 语法（`normal | <length>`）全接；但**模型是旧的**：规范 2026-08
  改成“每个字母两侧各放一半、行首行尾不加、跨 inline 取平均”，Hutu 仍是“每字符计一份”
  （`src/Hutu/Rendering/Layout/InlineLayout.cs:5374` 的 `SpacingUnits`，宽度在
  `src/Hutu/Rendering/Layout/InlineLayout.cs:4598` / `:5719`）。红对：`cursive-001/002`
  （阿拉伯文不得逐字母加间距，§7.2.1）、`ligatures-003`（非零间距应停用可选连字）、
  `letter-spacing-211`（着重号应以字符为中心而不是字符 + 间距）；`bengali-yaphala-001` 无法绘制。
- **`text-indent`**：只读 `<length-percentage>`（`src/Hutu/Rendering/ComputedStyle.cs:332`、
  `src/Hutu/Rendering/Layout/InlineLayout.cs:6056`）；Text 3 的 **`hanging` 与 `each-line`
  未实现**。3 个红对恰是 `text-indent-abspos-hanging-001`、`text-indent-each-line-hanging`、
  `text-indent-length-002`。

## 3. 属性之外的规范条款

- **§4 空白处理**：Phase I 折叠/转换、Phase II 行首行尾裁剪、segment break 转换、`tab-size`
  都实现了；17 个 `white-space` 红说明边界情形仍有差。
- **§5 断行**：UAX#14 规则集在 `src/Hutu/Rendering/Layout/LineBreaks.cs`（含 `anywhere`、
  `break-all`、`keep-all` 的开关）。§5.5“词法文字要有 fallback、不得溢出”未实现——
  `line-breaking-023..027` 5 对全红（与 `line-breaking` 文件夹的 5 对同名）。
- **§6 对齐与两端**：§6.1–6.3 完整；§6.4 方法部分见上；§6.4.4 连写文字不断开连接——
  Hutu 只在空格与 CJK 缝扩张，阿拉伯文不会被打散，这条满足；§6.4.5 最小机会集不完整
  （CJK ↔ 任意单元）。
- **§7 间距**：§7.1 完整；§7.2 值完整、新模型未跟上；**§7.3 跨元素边界整形**没有按规范做——
  Hutu 以元素为单位切 run 并合并（`src/Hutu/Rendering/Layout/InlineLayout.cs:7988`
  “one merge per element”），所以没有 margin/border/padding、`vertical-align`、isolation 时
  本应延续的连字/连接不会跨元素形成；css-text 语料中没有 §7.3 的直接用例。
- **§8 边缘效果**：§8.1 `text-indent` 见上；§8.2 悬挂字形（hanging glyph 不计入 fit、对齐、
  两端与内在尺寸）只在 `hanging-punctuation` 上实现，通用悬挂（斜体出框、`break-spaces`
  尾随空格）未建模，相关用例（`full-width-leading-spaces-004`、`hanging-scrollable-001`）未导入；
  §8.3 行盒基方向——probe `bidi-lines-001` 显示 6 行波斯语都停在 x=19（行首），
  **行盒基方向没有参与 text-align**，波斯语没有右对齐；另一对 `empty-span-001` 仍红
  （空 span 干扰 bidi 重排）。

## 4. 语料水分

reftest 的通过只说明两张图一致；两侧都空操作时也会一致。Text 3 范围内：

1. **`text-transform` 5/16 假通过**：`fullwidth-006/007` 依赖 U+3000 与空格在 Ahem 下同宽，
   `full-size-kana-005/006/007` 的断言本身就是“全角假名不变”。
2. **`hyphens-auto-001/002` 假通过**：断言分别是“无 lang 时不得自动断词”和“词本来就放得下”。
3. **`line-break` 的 strict/loose 没有任何语料**，通过率只证明 UAX#14 默认规则 + `anywhere`。
4. **`text-align-all` 没有语义语料**。
5. **`letter-spacing` 的新模型没有语料**：现有绿对按旧模型写，改了模型反而可能变红。

系统的假通过方法见 `conformance/vacuous-passes_zh_CN.md`。

## 5. 缺口排序（Text 3 内）

1. `text-justify`：`none` 与 `inter-character` 的非 CJK 扩张（0/5，已实测）。
2. `word-break` 的 tailor：东南亚/埃塞俄比亚词边界、`break-all` 与 UAX PR/GL 类（10 对）。
3. `white-space` 的边界情形：`break-spaces` 行首尾、`pre-wrap` 尾随空格、行边裁剪（17 对）。
4. `letter-spacing`：连写文字抑制、可选连字停用、新两侧各半模型（4 红 + 1 无法绘制）。
5. `text-indent` 的 `hanging` / `each-line`（3 对）。
6. `line-break` 的 `loose` / `strict` tailor（无有效语料）。
7. `text-transform` 的 `full-width` / `full-size-kana`（语料无有效覆盖）。
8. `text-align-all`、§7.3 跨元素整形、§8.2 通用悬挂字形（规范有、实现与语料都缺）。

## 附：各子目录当日结果

只列 Text 3 相关目录；`text-autospace` / `word-space-transform` / `text-fit` 分别属 Text 4 与
Text 5 / Inline 3，不在本次口径内。

| 子目录 | 总数 | 通过 | 失败 | 无法绘制 |
|---|---|---|---|---|
| bidi（§8.3） | 3 | 1 | 2 | 0 |
| hanging-punctuation | 17 | 17 | 0 | 0 |
| hyphens | 21 | 18 | 3 | 0 |
| letter-spacing | 9 | 4 | 4 | 1 |
| line-break | 91 | 82 | 9 | 0 |
| line-breaking（§5.5） | 50 | 45 | 5 | 0 |
| overflow-wrap | 44 | 36 | 8 | 0 |
| tab-size | 13 | 13 | 0 | 0 |
| text-align | 58 | 55 | 3 | 0 |
| text-indent | 14 | 11 | 3 | 0 |
| text-justify | 5 | 0 | 5 | 0 |
| text-transform | 18 | 16 | 2 | 0 |
| white-space | 254 | 233 | 21 | 0 |
| word-break | 62 | 47 | 15 | 0 |
| word-spacing | 2 | 2 | 0 | 0 |

## 复现

```powershell
# 全量计分（报告写到 build/reftest-score.md，每次覆盖）
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj `
  --filter "FullyQualifiedName~TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay" --no-build

# 只看 css-text 子集（秒级，分子分母会变，趋势一致）
$env:HUTU_SCORE_ONLY="css-text"
```

单页布局实测：

```powershell
$env:HUTU_PROBE_PAGE="D:/_git0/Hutu/conformance/reftests/wpt/css-text/text-justify/text-justify-inter-character-001.html"
$env:HUTU_PROBE_OUT="D:/_git0/Hutu/build/probe-justify.txt"
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj --filter "FullyQualifiedName~LayoutProbe" --no-build
```

用法与限制见 `docs/reftest-scoring_zh_CN.md`。
