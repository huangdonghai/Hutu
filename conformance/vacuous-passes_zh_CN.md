# 假通过清单：画对了，但没有测到东西

> 首次审计 2026-10-29，引擎 HEAD `9ca69b1a`，数据取自当日全量报告 `build/reftest-score.md`。

reftest 的"通过"只说明**测试页与参照页像素一致**。当某个特性在两侧都没生效时，两张图当然一致，
这种通过不携带信息量——它既不能证明特性实现了，也不能在特性被改坏时报警。

最常见的形成方式：测试页用了一个引擎没实现的属性（空操作），参照页用了一个手工等价手法
（也失效），两侧一起退回默认，于是"一致"。

`conformance/css-support.json` 记的是**属性级**状态（`modelled` / `partial` / `unsupported`），
回答"这个属性能不能用"；这份文件回答另一个问题——"这一对过了，但过有没有意义"。

## 判定方法

对当日**通过**的 6007 对（gecko + wpt）逐对读参照文件，命中任一即列为候选：

1. 参照页使用了引擎无法解析的长度单位，声明被整条丢弃：
   `ic`、`ric`、`cap`、`lh`、`rlh`、`cqw/cqh/cqi/cqb/cqmin/cqmax`；
2. 参照页使用了 `@supports` / `@container`（这两个 at-rule 里的规则目前不会被取用）；
3. 参照页声明了 `css-support.json` 里非 `modelled` 的属性，而测试页没有声明同名属性。

6007 对里有 5801 对能定位并读到参照文件，命中 **7 对**。

## 清单

| 用例 | 参照页里失效的声明 | 为什么通过是假的 | 修法 |
|---|---|---|---|
| `wpt/css-text/text-autospace/text-autospace-elements-004.html` | `margin: 0.125ic` | 测试页的 `text-autospace` 未实现（空操作），参照页的 `ic` 被丢弃（也空操作），两侧都是原样 | 补 `ic` 单位（`ic` = 水 U+6C34 的 advance）后，这 5 对会**如实变红**，等 `text-autospace` 实现再转绿 |
| `wpt/css-text/text-autospace/text-autospace-elements-005.html` | `margin-right: 0.125ic` | 同上 | 同上 |
| `wpt/css-text/text-autospace/text-autospace-elements-005b.html` | `margin: 0.125ic` | 同上 | 同上 |
| `wpt/css-text/text-autospace/text-autospace-elements-006.html` | `margin: 0.125ic` | 同上 | 同上 |
| `wpt/css-text/text-autospace/text-autospace-preformatted-001.html` | `margin-left: 0.125ic` | 同上 | 同上 |
| `wpt/css-anchor-position/anchor-fixed-pos-descendant.html` | `height: 1lh` | 测试页自己也写 `height: 1lh`，两侧一起被丢，比的是两个 `height: auto` 的盒子 | 补 `lh` / `rlh`（取元素与根元素计算后的 `line-height`） |
| `wpt/css-viewport/zoom/relative-units-from-parent.html` | `line-height: 4lh` | 测试页写 `2lh`、参照页写 `4lh`，两侧都丢，都退回 `line-height: normal`，仍然相等 | 同上 |

7 对占通过数 0.12%，不影响趋势判断，但它掩盖了 5 个 `text-autospace` 用例的真实状态：
当前那 19 对里"8 对通过"有 5 对是这种水分，实际有效通过只有 3 对。

## 排除的噪声

- `wpt/css-gaps/*-gap-decorations-*`（17 条）：测试页用 `gap` 简写、参照页用 `row-gap` / `column-gap` 长写，
  检测规则把"简写没有算作长写"误判成缺失，不是假通过。
- `wpt/css-nesting/supports-is-consistent.html`：`@supports` 只出现在 `<title>` 里，不是规则。

## 这套检测抓不到什么

上面三条规则只能抓**单边失效**。抓不到的是**对称失效**：两侧都依赖同一个未实现特性，
只是写法不同。这一类的识别需要按特性建等价手法表，已知的有：

| 被测特性 | 参照页常用的等价手法 |
|---|---|
| `text-autospace` | `margin: …ic` |
| `hanging-punctuation` | 负 `text-indent` 或负 `margin` |
| `text-spacing-trim` | `font-feature-settings: 'halt'` 包住标点 |
| `line-break: strict/loose` | 手工 `<br>` 断行 |
| `text-emphasis` 的关闭态 | 干脆不写（两侧都没强调，必然相等） |

已知实例：`wpt/css-text/hanging-punctuation/hanging-punctuation-with-bidi.html`——语料里唯一一对
`hanging-punctuation` 用例，未实现悬挂也能通过。

## 何时重跑

- 补上 `ic` / `lh` 一类长度单位之后：清单前 5 条应当变成失败，那是正确结果，不是回归；
- 每次改 `conformance/css-support.json` 之后；
- 每次重新导入语料之后（分母与文件都会变）。

扫描脚本是临时的，放在 `build/`（不入库）；重做时按上面三条判定规则实现即可，
输入是 `build/reftest-score.md` 里的 "What the engine already gets right" 分节。
