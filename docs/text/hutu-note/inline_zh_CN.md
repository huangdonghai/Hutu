# 行内夹注（双行注）

> 总纲见 [overview_zh_CN.md](overview_zh_CN.md)。本册只描述 `-hutu-note-placement: inline`。

## 1. 触发与结构

```css
.note { -hutu-note-placement: inline; }
```

```html
<p>正文<span class="note">注文内容</span>正文</p>
```

- 注文元素必须处于行内格式化上下文中。
- 一个注文元素生成**一个**夹注盒；夹注盒内部固定为两条子行。
- 注文盒排在注文元素所在的 DOM 位置：即紧接在它前面的行内内容之后、后面的行内内容之前。
- 每条子行都是一个普通行盒，使用普通行内排版、字形整形和基线计算；夹注不引入额外的字形网格。
- 夹注可以跨外层行；每个外层片段内部仍只有两条子行。
- 本节规则适用于任意文字系统，不限于 CJK。比例字体、连字、组合字符、emoji、RTL 和复杂文字整形都按普通行布局处理。

## 2. 几何

### 2.1 记号

- `n`：当前片段的注文按 `white-space` 处理后的排版字符数（按字素簇计，组合符号随基字）。
- `L`：注文所在行在普通一行排版下的**已用行高**（used `line-height`），计算时不计夹注盒。
- `f`：两个子行的实际字号（used `font-size`）。
- `h`：两个子行的实际行高（used `line-height`）。
- `w`：当前片段两条子行实际测量宽度的较大值。

### 2.2 字号与行高

作者对注文元素**直接声明**的 `font-size` 和 `line-height` 优先；继承值只用于缺省计算：

| 作者声明 | 实际值 |
|---|---|
| `font-size` 和 `line-height` 都声明 | `f = font-size`，`h = line-height` |
| 只声明 `font-size` | `f = font-size`，`h = f` |
| 只声明 `line-height` | `h = line-height`，`f = min(继承字号, L / 2)` |
| 两者都未声明 | `f = min(继承字号, L / 2)`，`h = f` |

- `line-height: normal` 按其普通规则解析为实际像素值。
- 作者显式声明的值不受 `L` 限制，也不做二次截断。
- 两者都未声明时，`2h = 2f ≤ L`；两子行保证落在行盒内。
- 作者显式声明后，`2h` 可以大于 `L`。超出部分按绘制溢出处理，夹注仍**不得**改变正文行高。
- 建议作者需要传统效果时显式写 `font-size: 0.5em`；不写时常见结果约为 `0.75em`（当 `L = 1.5em`）。

### 2.3 尺寸

| 量 | 值 |
|---|---|
| 每子行 inline 尺寸 | 按普通行内排版测得的该子行宽度 |
| 片段内容 inline 尺寸 | `w = max(第一子行宽度, 第二子行宽度)` |
| 片段 border box inline 尺寸 | `w` + 该片段承担的 inline padding／border |
| 片段 margin box inline 尺寸 | border box inline 尺寸 + 该片段承担的 inline margin |
| 每子行 block 尺寸 | `h` |
| 片段内容 block 尺寸 | `2h` |
| 片段 border box block 尺寸 | `2h` + block padding／border |
| 片段 margin box block 尺寸 | border box block 尺寸 + block margin |

- 两子行都按普通行布局测量宽度；较短的一行在 inline-end 留空，不拉伸、不补字形。
- CJK 等宽文本下，第一子行宽度通常为 `ceil(n / 2) × f`，第二子行为 `floor(n / 2) × f`，因此单片段推进退化为 `ceil(n / 2) × f`。这只是等宽文本的算术特例；任何非等宽文本都必须使用实际测量宽度。
- 跨行且无装饰时，总推进是各片段内容宽度之和 `Σ w_i`；有装饰时按 §2.5 使用各片段 margin box 的 inline 尺寸累加。每个片段独立对半分，所以多个奇数片段会比整条注文一次对半分多出字符分配，但不再引入“半格”概念。
- 两子行沿 block 轴并排：第一子行靠 **block-start**（竖排为右列，横排为上行），第二子行靠 **block-end**（竖排为左列，横排为下行）。
- 默认装饰下，注文盒的 margin box block 中心与注文所在行盒的 block 中心重合。
- 缺省字号和行高下，`2h ≤ L` 保证注文盒落在行盒内；它可以占用行距空白，但**不得**改变行盒高度。作者显式指定字号、行高或添加 `padding`／`border` 后可能超出，后果由作者承担。

### 2.4 字符分配

- 前 `ceil(n / 2)` 个字符进第一子行，其余 `floor(n / 2)` 个进第二子行。
- 注文为奇数字时，第一子行多一字，第二子行在 inline-end 留空一个字符位。
- 两子行的 inline 起点对齐；第二子行较短时在 inline-end 留空。
- “第一半”按逻辑顺序的字素簇计算，不按视觉顺序；RTL 文本在每条子行内部再由 bidi 重排。
- 对半分配发生在字素簇边界，可以切断拉丁词等连续文本；每条子行随后按普通行内规则整形，但不在片段内部再次折行。

### 2.5 盒装饰与跨片段

注文盒按普通 inline 盒的分片模型处理。`margin`、`padding`、`border` 参与几何，`background` 按 border box 绘制；默认使用 `box-decoration-break: slice`：

- **单片段**：片段同时承担 inline-start、inline-end 以及 block-start、block-end 的全部装饰。
- **多片段**：
  - 第一个片段只承担 inline-start 的 margin／padding／border；
  - 最后一个片段只承担 inline-end 的 margin／padding／border；
  - 中间片段不承担 inline-start／end 的 margin／padding／border；
  - 每个片段都承担自己的 block-start／block-end margin／padding／border。
- `box-decoration-break: clone` 时，每个片段都承担完整的 inline-start／end 与 block-start／end 装饰。
- 背景和边框按片段绘制；box-decoration-break 为 `slice` 时，首尾边缘只在整条注文的首尾出现。
- 装饰不改变 `f` 和 `h`，只改变片段的 border box、margin box、推进和绘制范围。
- 装饰后的 margin box block 尺寸大于 `L` 时，注文可以超出行盒；正文行高仍保持不变。

### 2.6 前后空隙

- 默认**无空隙**：注文盒紧接前一行内内容，后一行内内容紧接注文盒。
- 作者在注文元素上声明的 `margin`／`padding`／`border` 按 §2.5 加入推进；此时空隙由作者负责。

## 3. 断行与片段

1. 在当前外层行的剩余 inline 空间中，取能在两条普通子行内排下的**最大字素簇前缀**。
2. 将该前缀按字符数对半分：第一子行取 `ceil(n / 2)` 个字符，第二子行取 `floor(n / 2)` 个字符。
3. 两条子行必须各自在剩余 inline 空间内排成一行；夹注片段内部不再二次折行。
4. 片段的 inline 推进取两条子行实际测量宽度的较大值，再加该片段应承担的装饰。
5. 当前外层行已有内容、但连一个字符的片段都放不下时，整个片段移到下一外层行。
6. 空外层行仍放不下一个字符时，放置一个字符并允许 inline 溢出，以保证排版总能向前推进。
7. 剩余注文从下一片段继续，重复上述过程；夹注不限长，可以跨任意多行／多列。
8. 每个片段独立对半分。片段之间的阅读顺序为：片段 1 第一子行 → 片段 1 第二子行 → 片段 2 第一子行 → ……，与 DOM 顺序一致。

### 3.1 示例

正文 `font-size: 16px`、`line-height: 24px`；作者为注文声明 `font-size: 8px`，未声明 `line-height`：

- `f = 8px`，`h = f = 8px`，`2h = 16px ≤ 24px`。
- 注文 7 字：第一子行 4 字、第二子行 3 字；等宽 CJK 下宽度分别为 `32px` 和 `24px`，片段推进为 `32px`。
- 若当前行只能容纳 3 个等宽字符的子行：片段 1 取 6 字（3 + 3），片段 2 取 1 字（1 + 0）。

## 4. 边界情况

| 情况 | 行为 |
|---|---|
| `n = 0` | 不生成注文盒，等价于 `none` |
| `n = 1` | 第一子行 1 字，第二子行空；片段 block 尺寸仍为 `2h` |
| 奇数字 | 第一子行多一字 |
| 连续空格 | 按 `white-space` 的折叠规则处理后再计 `n` |
| 注文内有 `<br>` | 结束当前外层片段；后续内容从下一片段继续，并在该片段内重新对半分 |
| 注文内有 ruby | V1 不要求支持；作者应避免在夹注内使用 ruby。若引擎仍将其排入，注音不属于本规范的布局保证。 |
| 注文出现在 `rt` 内 | `-hutu-note-placement` 不生效，按 `none` 处理 |
| 注文内有嵌套注文 | 内层不生效，按普通行内内容排 |
| RTL 文本 | 每条子行按普通 bidi 规则排版；子行顺序仍按 block-start／end（见总纲 §8） |
| 组合字符／emoji | 一个扩展字素簇算一个字符，不拆开组合序列或 ZWJ 序列 |
| 比例字体／复杂文字 | 两子行分别按普通 shaping 测量；字符数多的一行不一定更宽 |

## 5. 与其它特性的交互

- **行高**：夹注不改变注文所在行的行高；它只在行盒内占位与绘制。
- **`text-emphasis`**：按普通行内布局在子行内排版。
- **`text-decoration`**：按普通行内盒和片段规则绘制；默认 `box-decoration-break: slice`。
- **`text-spacing-trim` / `hanging-punctuation`**：在每条子行内按普通行规则处理。
- **`text-combine-upright`**：在每条子行内按普通行规则处理。
- **浮动与绝对定位**：注文元素上的 `float`／`position` 被忽略。

## 6. 验收点

验收点与测试类型如下：

> 类型标记：**探针** = 数值几何断言；**Reftest** = 与参考页做带文字抗锯齿容差的截图比较；**探针 + Reftest** = 两者均必须。

1. **探针 + Reftest**：单片段时，注文盒 inline 推进 = `max(第一子行宽度, 第二子行宽度)`（默认装饰下）；等宽 CJK 下退化为 `ceil(n/2) × f`；跨行时按片段累加。
2. **探针**：片段内容 block 尺寸 = `2h`；border box 和 margin box 按 §2.3、§2.5 加上装饰。缺省字号和行高下 `2h ≤ L`，作者显式指定值后允许超出。
3. **探针 + Reftest**：第一子行字符数 = `ceil(n/2)`，第二子行 = `floor(n/2)`。
4. **探针 + Reftest**：每个片段取能排下的最大字素簇前缀，并独立对半分；子行内部不再折行。
5. **探针 + Reftest**：注文所在行的行高与无注文时完全相同。
6. **探针 + Reftest**：`n = 1`、奇数字、空注文三种边界。
7. **探针 + Reftest**：跨 2 行、跨 3 行以上时，片段顺序与 DOM 顺序一致。
8. **探针 + Reftest**：作者设置 `margin`／`padding`／`border` 时，首片段、中间片段、末片段分别承担正确的 inline 边缘；block 边缘每个片段都有；背景和边框按 `box-decoration-break` 绘制。
9. **探针 + Reftest**：作者声明 `font-size`／`line-height` 的四种组合均按 §2.2 生效；显式值超过 `L` 时只溢出，不改变正文行高。
10. **探针 + Reftest**：非 CJK、比例字体、组合字符和 RTL 文本均按普通 shaping 测量；使用两子行的实际测量宽度，不套用 `ceil(n/2) × f`。字符数较多的第一子行可以比第二子行更窄。

## 7. 样例

```html
<style>
  .page { writing-mode: vertical-rl; font-size: 16px; line-height: 1.5; }
  .note { -hutu-note-placement: inline; font-size: 0.5em; line-height: 1; }
</style>
<p class="page">司馬相如者，蜀郡成都人也，字長卿<span class="note">案：長卿，相如之字也。</span>。</p>
```
