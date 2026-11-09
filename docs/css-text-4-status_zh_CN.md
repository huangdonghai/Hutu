# CSS Text Module Level 4 实现盘点

> 审计日期 2026-10-30，引擎 HEAD `7357d65a`。这是一次**只读盘点**：数据来自读代码与当日全量语料运行，
> 没有为这份文档改任何代码。语料数字取自 `build/_folders2.tsv`，红对名单取自 `build/_folders2-fails.txt`。

## 0. 口径与方法

- 属性集合取自今日抓取的 <https://drafts.csswg.org/css-text-4/>，按该模块**自己定义**的 propdef 计：
  31 条 propdef 加上 `text-spacing` 简写，`wrap-before` / `wrap-after` 各算一条，共 **34 个属性**。
  `word-wrap` 是 `overflow-wrap` 的旧别名，按别名处理，不单列。
- 代码证据给到 `文件:行`；语料证据给到 `css-text/<子目录> 通过/总数` 与红对文件名。
- 当日全量：gecko 1709/2335（73%），wpt 4319/5845（73%），wpt-variants 85/85（100%）。
- `css-text/text-fit` 的 10 对全部是失败，但它测的是 **CSS Text 5 的 `text-fit` 与 CSS Inline 3 的 `text-box`**，
  不属于本模块，不计入下面的表。

## 1. 总表

| 状态 | 属性 | 语料 |
|---|---|---|
| 完整 | `text-spacing-trim` | 78/78 + 变体 6/6 |
| 完整 | `hanging-punctuation` | 17/17 + 变体 1/1 |
| 完整 | `tab-size` | 13/13 |
| 完整 | `word-spacing` | 2/2（含百分比对，非水分） |
| 语法完整、行为仍有红 | `text-autospace` | 21/26 |
| 语法完整、行为仍有红 | `text-align-last` | 无独立红对 |
| 语法完整、行为仍有红 | `hyphenate-character` | 3/4 |
| 部分实现 | `text-transform` | 16/18（其中 5 对假通过） |
| 部分实现 | `white-space` | 233/254 |
| 部分实现 | `text-wrap` / `text-wrap-style` | 见 white-space 文件夹红对 |
| 部分实现 | `word-break` | 47/62 |
| 部分实现 | `line-break` | 82/91（语料只测 `anywhere`） |
| 部分实现 | `hyphens` | 18/21（2 对假通过） |
| 部分实现 | `overflow-wrap` | 36/44 |
| 部分实现 | `text-align` | 55/58 |
| 部分实现 | `text-justify` | 0/5 |
| 部分实现 | `letter-spacing` | 4/9 + 1 无法绘制 |
| 部分实现 | `text-indent` | 11/14 |
| 未实现 | `word-space-transform` | 9/9 全为假通过 |
| 未实现 | `white-space-collapse` | — |
| 未实现 | `white-space-trim` | — |
| 未实现 | `text-wrap-mode` | — |
| 未实现 | `wrap-inside` | — |
| 未实现 | `wrap-before` / `wrap-after` | — |
| 未实现 | `text-align-all` | — |
| 未实现 | `text-group-align` | — |
| 未实现 | `line-padding` | — |
| 未实现 | `text-spacing`（简写） | 语料无用例 |
| 未实现 | `hyphenate-limit-zone` / `-chars` / `-lines` / `-last` | — |

## 2. 逐项证据

### 2.1 完整实现

- **`text-spacing-trim`**：`<spacing-trim> | auto` 全语法（`normal`、`space-all`、`space-first`、
  `trim-start`、`trim-both`、`trim-all`）从 `src/Hutu/Rendering/Layout/TextSpacingTrim.cs:63` 起解析；
  水平/竖直、测量/绘制/内在尺寸都接了 delta+mask 路线。语料唯一全绿的大项。
- **`hanging-punctuation`**：`none | [ first || [ force-end | allow-end ] || last ]` 全语法在
  `src/Hutu/Rendering/Layout/HangingPunctuation.cs:41` 起解析，悬挂在 `InlineLayout.SettleLines` 中
  按 start → end 顺序执行。17/17 全绿（Chrome 的 `CSS.supports("hanging-punctuation","first")` 为 false，
  语料参考图是唯一真值）。
- **`tab-size`**：数与长度两种形式都在 `src/Hutu/Rendering/ComputedStyle.cs:2270` 解析，13/13。
- **`word-spacing`**：`normal | <length-percentage>`，百分比按 font-size 折算
  （`src/Hutu/Rendering/ComputedStyle.cs:260`），2/2 且百分比对确实起作用。

### 2.2 语法完整、行为有缺口

- **`text-autospace`**：`normal | <autospace> | auto` 全语法（三类邻居 + insert/replace + 旧
  `no-autospace`）在 `src/Hutu/Rendering/Layout/TextAutospace.cs:55` 起解析。5 个红对：
  `text-autospace-004`（sideways-lr，文档 Ahem 加载后才暴露）、`elements-007`、`first-line-001`
  （测量未套用 first-line 样式）、`mixed-001`、`vs-001`。
- **`text-align-last`**：`auto | start | end | left | right | center | justify | match-parent` 全在
  `InlineLayout.AlignLastLines`（`src/Hutu/Rendering/Layout/InlineLayout.cs:5874`），含 `auto`
  对 justify 的降级规则。
- **`hyphenate-character`**：`auto | <string>` 在 `src/Hutu/Rendering/ComputedStyle.cs:4032` 解析。
  唯一红对 `hyphenate-character-005` 需要自动断词 + RTL 连字整形，属 `hyphens` 缺口而非本属性。

### 2.3 部分实现

- **`text-transform`**：只做 `none/uppercase/lowercase/capitalize`（含希腊 tonos/dialytika 定制），
  见 `src/Hutu/Rendering/Boxes/TextTransformResolver.cs:53`。`full-width`、`full-size-kana`、
  `math-auto` 在代码里没有任何引用。
- **`white-space`**：只认六个旧整值 `normal/nowrap/pre/pre-wrap/pre-line/break-spaces`
  （`src/Hutu/Rendering/ComputedStyle.cs:4567` 起的判定），没有 Text 4 的长写语法
  （`white-space-collapse` 的 `discard`/`preserve*`、`white-space-trim`、`text-wrap-mode`）。
  21 个红对集中在 `break-spaces` 行首/行尾、`pre-wrap` 尾随空格、`line-edge-white-space-collapse`、
  `text-wrap-balance` 浮动与 line-clamp、`intrinsic-size`。
- **`text-wrap` / `text-wrap-style`**：值表在 `src/Hutu/Rendering/ComputedStyle.cs:4345`，
  只接受 `auto/balance/stable/pretty`；只有 `balance` 被读取（`InlineLayout.cs:142`），
  `stable`/`pretty` 解析后无行为，新值 `avoid-short-last-line` 不接受；`text-wrap-mode` 完全缺失。
- **`word-break`**：`normal/break-all/keep-all/break-word` 都读，`manual` 落入 normal 路径，
  `auto-phrase` 仅用于「有软连字符时放弃断词」（`InlineLayout.cs:913`、`InlineLayout.cs:3192`），
  没有词组切分。15 个红对：5 个 `auto-phrase`、`break-all-023`/`ethiopic`/`inline-008`、
  `keep-all-011`、`normal-002/003`/`ethiopic`/`th-001`、`min-content-007`、`break-boundary-2-chars-001`。
- **`line-break`**：只区分 `anywhere`（`ComputedStyle.cs:4627`）；`auto/normal/loose/strict`
  走同一套 UAX#14 规则，没有 strict/loose 的 tailor。**语料对此无覆盖**：`css-text/line-break`
  的 82 个文件里 119 处声明全部是 `anywhere`，所以 82/91 不能说明 strict/loose。
  红对为 `anywhere-overrides-uax-behavior-009/010/013/014` 与 `line-breaking-023..027`。
- **`hyphens`**：`none/manual/auto` 都存下来（`ComputedStyle.cs:843`），`none` 丢软连字符
  （`ComputedStyle.cs:884`），但**没有自动断词词典/算法**，`auto` 只是允许软连字符。
  红对 `hyphenate-character-005`、`hyphens-overflow-001`、`hyphens-vs-float-clearance-002`。
- **`overflow-wrap`**：`normal/break-word/anywhere` 全读（`ComputedStyle.cs:4679`），
  并区分了 anywhere 与 break-word 的 min-content。8 个红对：`anywhere-010`、
  `anywhere-inline-004`、`anywhere-span-001`、`break-word-010`、`break-word-span-001`、
  `cluster-001/002`、`min-content-size-009`，集中在字素簇、内联边界与内在尺寸。
- **`text-align`**：有 `start/end/left/right/center/justify/match-parent`，`justify-all` 归一为
  justify+justify（`ComputedStyle.cs:354`、`ComputedStyle.cs:359`），偏移在
  `InlineLayout.AlignmentOffset`（`InlineLayout.cs:6070`）。**`<string>` 值未实现**，
  `text-align-all` 未实现。红对 `start-016`/`end-016`（`pre` + `dir=auto` 首强字符方向）、
  `justify-bidi-control`。
- **`text-justify`**：只认 `auto/inter-word/inter-character/distribute` 四种扩张点
  （`src/Hutu/Rendering/Layout/TextClusters.cs:319`）；**`none`、`ruby`、`no-compress` 缺失**。
  0/5 全红：`006`、`distribute-001`、`inter-character-001`、`inter-word-001`、`none-001`，
  多为 `text-align-last: justify` 下的扩张算法差异。
- **`letter-spacing`**：值与百分比都读，但只做「每字加宽」（`InlineLayout.cs:4598`），
  **没有按规范中止连字/花体连接**。红对 `letter-spacing-211`、`cursive-001/002`、`ligatures-003`，
  另 1 对无法绘制。
- **`text-indent`**：只读 `<length-percentage>`（`ComputedStyle.cs:332`、`InlineLayout.cs:6056`）；
  **`hanging` 与 `each-line` 未实现**。红对恰好是 `text-indent-abspos-hanging-001`、
  `text-indent-each-line-hanging`、`text-indent-length-002`。

### 2.4 完全未实现（0 处代码引用）

`word-space-transform`、`white-space-collapse`、`white-space-trim`、`text-wrap-mode`、
`wrap-inside`、`wrap-before`、`wrap-after`、`text-align-all`、`text-group-align`、`line-padding`、
`text-spacing`（trim+autospace 的简写，语料无用例）、四个 `hyphenate-limit-*`。

## 3. 需要特别标注的「水分」

reftest 的通过只说明两张图一致；两侧都空操作时也会一致。以下通过不携带信息：

1. **`word-space-transform` 9/9 全假**：9 个用例全是「`auto-phrase` 非法 / 不得插入」的否定断言
   （例如 `word-space-transform-020` 的参考页根本没有该属性），属性未实现反而正好通过。
2. **`text-transform` 的 5 对**：`fullwidth-006/007` 依赖 U+3000 与空格在 Ahem 下同宽，
   `full-size-kana-005/006/007` 的断言本身就是「全角假名不变」。真正验证的只有大小写与希腊定制。
3. **`hyphens-auto-001/002`**：断言分别是「无 lang 时不得自动断词」和「词本来就放得下」，
   自动断词缺失不影响结果。
4. **`line-break` 的 strict/loose 没有任何语料**，当前通过率只证明 UAX#14 默认规则 + `anywhere`。

更系统的方法与既有清单见 `docs` 同目录的假通过审计（`conformance/vacuous-passes_zh_CN.md`）。

## 4. 缺口排序（按语料影响与规范位置）

1. `text-justify` 的 `none`/`ruby`/`no-compress` 与 justify 扩张算法（0/5）。
2. `word-break: auto-phrase` 的词组切分（5 对）与 `break-all`/`keep-all` 的 tailor 细节（10 对）。
3. `white-space` 的 Text 4 长写（`white-space-collapse`/`white-space-trim`/`text-wrap-mode`）
   与 `break-spaces` 行首/行尾（21 对）。
4. 自动断词 `hyphens: auto`（无词典）与 `hyphenate-limit-*`。
5. `text-transform` 的 `full-width`/`full-size-kana`/`math-auto`（语料无有效覆盖，需先补参照）。
6. `text-align` 的 `<string>`、`text-align-all`；`text-group-align`、`line-padding`、
   `wrap-*`、`word-space-transform` 尚无实现也无语料。

## 附：各 `css-text/*` 子目录当日结果

| 子目录 | 总数 | 通过 | 失败 | 无法绘制 |
|---|---|---|---|---|
| bidi | 3 | 1 | 2 | 0 |
| hanging | 17 | 17 | 0 | 0 |
| hyphens | 21 | 18 | 3 | 0 |
| letter-spacing | 9 | 4 | 4 | 1 |
| line-break | 91 | 82 | 9 | 0 |
| line-breaking | 50 | 45 | 5 | 0 |
| overflow-wrap | 44 | 36 | 8 | 0 |
| tab-size | 13 | 13 | 0 | 0 |
| text-align | 58 | 55 | 3 | 0 |
| text-autospace | 26 | 21 | 5 | 0 |
| text-fit（Text 5 / Inline 3，不计入盘点） | 10 | 0 | 10 | 0 |
| text-indent | 14 | 11 | 3 | 0 |
| text-justify | 5 | 0 | 5 | 0 |
| text-transform | 18 | 16 | 2 | 0 |
| white-space | 254 | 233 | 21 | 0 |
| word-break | 62 | 47 | 15 | 0 |
| word-space-transform | 9 | 9 | 0 | 0 |
| word-spacing | 2 | 2 | 0 | 0 |
| variants/text-spacing-trim | 78 | 78 | 0 | 0 |
| variants/text-autospace | 6 | 6 | 0 | 0 |
| variants/hanging | 1 | 1 | 0 | 0 |

## 复现

```powershell
# 全量计分（报告写到 build/reftest-score.md，每次覆盖）
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj `
  --filter "FullyQualifiedName~TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay" --no-build

# 只看 css-text 子集（秒级，分子分母会变，趋势一致）
$env:HUTU_SCORE_ONLY="css-text"
```

子目录表与红对名单是当日全量报告按目录汇总的结果。用法与限制见 `docs/reftest-scoring_zh_CN.md`。
