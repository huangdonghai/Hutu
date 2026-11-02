# 边注（旁注／眉批／地脚批）

> 总纲见 [overview_zh_CN.md](overview_zh_CN.md)。本册只描述 `-hutu-note-placement: margin`。

## 1. 触发与落位

```css
.note {
  -hutu-note-placement: margin;
  -hutu-note-side: auto; /* auto | inline-start | inline-end */
}
```

| `-hutu-note-side` | 位置 |
|---|---|
| `auto` | 比较根元素 `html` 两侧 margin 的静态可用厚度，取较大的一侧；相等时取 `inline-start` |
| `inline-start` | 正文的 inline-start 侧：横排为左边，竖排为上边（天头） |
| `inline-end` | 正文的 inline-end 侧：横排为右边，竖排为下边（地脚） |

- 注文元素必须处于行内格式化上下文中，其 DOM 位置即注文位置。
- 一个注文元素生成**一组**边注行（竖排为列），整体放在边注区。
- `inline-start`／`inline-end` 按**正文块容器**的 `writing-mode`／`direction` 解析，不按注文元素自身的对应声明解析。
- 边注内部的 `writing-mode`／`direction` 同样强制采用正文块容器的值；注文元素自身的对应声明忽略。
- 作者显式声明一侧时必须使用该侧，即使它的空间小于另一侧。
- `auto` 比较的是根元素 `html` 两侧 margin 区沿正文 inline 轴的**静态厚度**，不考虑已放置边注占用的剩余空间，也不根据注文内容的自然尺寸反推。
- 边注不改变正文的行高、行宽与位置。

### 1.1 位于其它注文内

- 行内夹注和行间注内部声明的边注受支持，必须提升到页面边注区。
- 在外层注文内容中，被提升边注保留零宽注文位置，不占外层注文的 inline 推进，也不在外层注文内部建立第二个边注区。
- 外层是行内夹注时，边注的声明行取外层夹注片段中包含该零宽位置的那条正文 line box。
- 外层是行间注时，边注的声明行取包含该零宽位置的行间注槽所对应的正文 line box。
- 被提升边注保留原 DOM 位置的 CSS computed style；`writing-mode`／`direction` 仍强制采用正文块容器的值。
- 边注内部再声明边注、行间注或行内夹注时，内层注文定义全部忽略，按普通节点参与边注内容排版。
- 其余未声明的嵌套组合也按普通节点处理，不建立额外的边注区或注文流。

## 2. 边注区

### 2.1 动态探测边注区

边注区不读取宿主是否分页。每条边注从根元素 `html` 的页边距开始，再向声明行所在的正文块动态探测实际可用的连续 margin 空白：

1. **基线**：页面／初始包含块边缘到 `html` border box 之间的对应侧 margin 区始终可用。`inline-start`／`inline-end` 按正文块容器的 `writing-mode`／`direction` 解析；若 `html` 自身仍按另一书写模式计算了逻辑边，应先映射到正文 inline 轴。
2. **声明块边界**：声明行所在的直接块容器的 border box 是该次探测的内容边界。可用的内边界取页面边缘到该 border box 对应侧外缘之间的空白。
3. **取并集**：最终边注带厚度取 `max(根 margin 基线厚度, 页面边缘到声明块 border 外缘的厚度)`。探测只能加宽，不能因为内层元素外凸或负 margin 而缩到根 margin 以内。
4. **只使用 clear margin**：探测不进入声明块的 border、padding、内容或背景绘制区。`body`、中间块和声明块依次形成的 margin 空白都可以计入；实际边界由布局后的 border box 几何决定，不另写 CSS 盒模型运算。
5. 零或负 margin 不形成额外空白。`margin: auto` 先按普通布局解析为 used margin；块 border 落入根 margin 内或越出页面时，回退到根 margin 基线。
6. `-hutu-note-side: auto` 的**选侧**仍然只比较 `html` 两侧 margin 的静态厚度，不因内层声明块的动态空白改侧；选侧后再在该侧执行动态加宽。
7. 同一注文组的整个自然流使用同一个探测厚度。不同声明块可以有不同厚度；同侧堆叠仍按声明行的正文 block 轴位置排序。

### 2.2 边注区的轴

边注区是一个矩形，外侧位置和带的长轴按**正文块容器**的书写模式解释：

- 横排正文：边注带位于左边或右边，是沿正文 block 轴（向下）延伸的竖带；横轴厚度由根 margin 与声明块 margin 空白共同决定。
- 竖排正文：边注带位于上边或下边，是沿正文 block 轴（`vertical-rl` 向左、`vertical-lr` 向右）延伸的横带；竖轴厚度由根 margin 与声明块 margin 空白共同决定。

边注内容的行、列方向与正文块容器一致，注文自身的对应声明不参与布局。边注区在正文 inline 轴上的尺寸决定**一行（列）能排多长**；正文 block 轴是自然流式延伸方向，不用容量反推边注落侧。

### 2.3 自然流式布局

- 边注组按普通自然流式块布局，不进行 shrink-to-fit，也不先测量内容自然宽度再收缩注文盒。
- `width: auto` 时，内容使用边注区在正文 inline 轴上的可用尺寸，文本按普通行盒规则自然断行；每行使用该可用 inline 尺寸进行排版。
- 显式 `width`／`height` 时按普通 CSS 盒模型处理。
- `margin`／`padding`／`border` 先占据边注区的可用空间，剩余部分作为内容区；同侧堆叠使用整个 margin box 的占用区间。
- 边注内部必须复用普通行内排版核心，不单独实现断行器或标点处理器。
- shaping、bidi、`white-space`、断行机会、标点禁则、`text-autospace`、`text-spacing-trim`、`hanging-punctuation`、着重号和普通行内装饰均按注文的普通行盒处理。
- 边注与普通正文的差异只包括可用 inline 尺寸、外部落位、边注自身的 computed style，以及强制采用正文书写模式。

## 3. 定位与换行

### 3.1 书写模式

边注的 `writing-mode` 与 `direction` 强制采用正文块容器的 computed value：

- 竖排正文生成竖排边注，每列从正文 inline-start 排向 inline-end。
- 横排正文生成横排边注，每行从正文 inline-start 排向 inline-end。
- 注文元素自身声明不同的 `writing-mode`／`direction` 时忽略该声明，不建立另一套书写轴或 base direction。

### 3.2 起始位置

边注组外接 margin box 只约束正文 block 轴上的起点：

1. 注文组的 block-start margin edge 与声明行的 line box block-start 对齐：
   - 横排：边注组的上 margin edge 对齐正文行顶部；
   - `vertical-rl`：边注组的右 margin edge 对齐正文列右侧；
   - `vertical-lr`：边注组的左 margin edge 对齐正文列左侧。

这里使用声明行最终布局后的 line box，不使用字符 ink box、基线或注文自身的“中心线”。从该起始边出发，注文组沿正文 block-end 方向继续展开；另一轴不另设边注带边缘定位，按普通块盒的自然 inline 流处理。

### 3.3 换行

1. 注文的行（列）沿正文 **inline 轴**从其 inline-start 排向 inline-end，长度受边注区在正文 inline 轴上的尺寸限制。
2. 一行（列）排满后，沿正文 **block 轴**向 block-end 换行；方向由正文书写模式决定。
3. block 轴方向不设边注容量限制，行（列）一直沿正文 block-end 继续。
4. 若边注区在 inline 轴上的尺寸连一个字符都放不下，注文沿正文 inline-end 方向自然延伸，不改换边注侧。

### 3.4 竖排示例

正文 `vertical-rl`，天头 `margin-top: 24px`，眉批 `font-size: 8px; line-height: 1`：

- 每列最多 `24 / 8 = 3` 个字；
- 写满 3 字后向左换列；
- 后续列继续向左排，不因到达天头或页面边界而换侧。

## 4. 堆叠与避让

### 4.1 同一侧多条

1. 同一条边上的多条边注按声明行最终 line box 在正文 block 轴上的位置排序：先取正文 block-start 侧的行，再向 block-end 排列；声明行位置相同再按 DOM 顺序。
2. 每条边注先按 §3.2 取得起始位置；若与前一条占用的正文 block 轴区间冲突，则沿正文 block-end 方向后移，**不得重叠**。发生冲突后不再要求它仍对齐声明行的 block-start。
3. 多条边注之间默认无空隙；需要空隙时由作者用 `margin` 设置。
4. 不同侧的边注互不影响：inline-start 与 inline-end 各自独立堆叠。

### 4.2 流式延伸

- 边注侧只由 `-hutu-note-side` 和 `auto` 的静态选择规则决定；内容长度、与已有边注的冲突或是否越过边注带边界都不改变边注侧。
- 边注组沿正文 block-end 方向持续自然流式排列。横排继续向下，`vertical-rl` 继续向左，`vertical-lr` 继续向右。
- 越过边注区或页面边界不是“放不下”，不触发换边。可见性按普通包含块和 `overflow` 规则处理。
- 边注带 inline 轴过薄时，单行（列）沿正文 inline-end 自然延伸。

## 5. 分页边界

- 边注布局不读取宿主的分页状态，也不对页边距做特殊归属、换边、换页或续排判断。
- 页面边界与边注区边界一样只是普通几何边界；越过时继续沿正文 block-end 自然延伸。
- 如果宿主最终对边注绘制区域做分片或裁剪，按普通包含块和 `overflow` 规则处理，不改变边注侧。

## 6. 字体、行高与行内对齐

- 未声明 `font-size` 时按 CSS 继承规则取得。
- 建议显式写 `font-size: 0.5em`；传统眉批约为正文的一半。
- `line-height` 未声明时为 `normal`，按注文字体计算。
- 字号与行高都会影响每行（列）的排版长度和注文组的实际延伸范围。
- 边注的 `text-indent` 使用注文元素的 computed value；它按普通 CSS 继承规则取得，可以继承正文祖先的值。该值只缩进边注组的第一条格式化行（列）。
- 边注的 `text-align` 使用注文元素的 computed value；它同样按普通 CSS 继承规则取得，由普通行盒在该注文的可用 inline 尺寸内执行。
- 边注不从被注正文行复制 `text-align` used value，也不为 `text-indent` 设置特殊的“正文缩进区”限制。两者的声明、继承、百分比与其它计算行为均和普通属性一致。

## 7. 与正文及其它特性的交互

- **正文布局**：边注不参与正文的行高、行宽、浮动避让与分栏计算。
- **行内排版核心**：边注复用普通行内断行、禁则、挤压、悬挂、bidi、shaping 和文本装饰逻辑，只替换可用宽度、外部起点与书写轴来源。
- **嵌套注文**：夹注和行间注内部的边注提升到页面边注区；边注内部的任何注文定义、夹注内部的夹注定义均忽略，按普通节点排版。
- **选区与复制**：按 DOM 顺序参与，边注文字出现在注文位置。
- **注文隐藏**：注文元素被 `display:none`／移除时，边注消失。
- **装饰**：`margin`／`padding`／`border`／`background` 全部尊重，计入边注占用的区间。
- **多栏**：边注区先取根元素 `html` 的 margin，再按声明块探测栏内的连续 margin 空白；不进入声明块的 border、padding 或内容区。
- **绝对定位**：注文元素上的 `position` 被忽略。

## 8. 验收点

> 类型标记：**探针** = 数值几何断言；**Reftest** = 与参考页做带文字抗锯齿容差的截图比较；**探针 + Reftest** = 两者均必须。

1. **探针**：边注区至少覆盖根元素 `html` 对应侧的 margin 区；声明块 border box 比 `html` 更靠内时，边注带动态加宽到该 border 外缘。最终厚度 = `max(根 margin 基线, 页面边缘到声明块 border 外缘)`，且不进入 border、padding 或内容区。
2. **探针 + Reftest**：横排正文的边注位于左／右 margin 区，竖排正文的边注位于上／下 margin 区；`inline-start`／`inline-end` 随正文 `writing-mode`／`direction` 解析；注文自身声明不同的 `writing-mode`／`direction` 不得改变落侧、行内方向或换行方向。
3. **探针 + Reftest**：`-hutu-note-side: auto` 按 `html` 两侧正 margin 沿正文 inline 轴的静态厚度选择较大侧；已放置边注不得改变选择结果；相等时取 `inline-start`；显式侧不被自动改写。
4. **探针 + Reftest**：注文组在正文 block 轴上的起始 margin edge 与声明行 line box 的 block-start 重合；横排、`vertical-rl`、`vertical-lr` 分别验证。
5. **探针 + Reftest**：竖排眉批写满天头厚度后向左换列；横排写满左右边注区宽度后向下换行。
6. **探针 + Reftest**：同侧多条按声明行的正文 block 轴位置排序，同一行再按 DOM 顺序；堆叠不重叠，发生冲突的后续注文只向正文 block-end 方向后移。
7. **探针 + Reftest**：内容较长、越过边注带边界或与前一条边注冲突时，边注侧保持不变，内容沿正文 block-end 继续延伸。
8. **探针 + Reftest**：边注不改变正文任何行的位置。
9. **Reftest**：竖排、横排各一组用例。
10. **探针 + Reftest**：边注 `font-size` 与 `line-height` 改变时，每行（列）的断行位置和注文组总延伸范围随之改变。
11. **探针 + Reftest**：边注的 `text-indent` 使用正常 CSS 继承后的注文 computed value，只影响注文组第一条格式化行（列）。
12. **探针 + Reftest**：边注的 `text-align` 使用正常 CSS 继承后的注文 computed value，并在注文自身的可用 inline 尺寸内执行；正文行最终的 `text-align` used value不得覆盖它。
13. **探针 + Reftest**：同一段文字作为正文和边注时，断行机会、标点禁则、`text-autospace`、`text-spacing-trim`、`hanging-punctuation`、bidi 和 shaping 结果一致；只有可用 inline 尺寸、外部落位和注文自身的 computed style 可以不同。
14. **探针 + Reftest**：夹注和行间注内部声明的边注提升到页面边注区，零宽位置不占外层注文推进，并按对应外层注文的正文行确定声明行；边注内部再声明任何注文时，内层定义按普通节点忽略。
15. **探针 + Reftest**：横排和竖排页面分别设置 `html` margin、`body`/中间块 margin 和声明块 margin；边注厚度逐层加宽到声明块 border 外缘。声明块 border 落入根 margin 内时不得窄于根 margin；`auto` 选侧结果仍只由 `html` 两侧 margin 决定。

## 9. 样例

```html
<style>
  html {
    writing-mode: vertical-rl;
    margin: 24px 32px;        /* 天头/地脚 24px，左右 32px */
    font-size: 16px;
    line-height: 1.5;
  }
  body {
    margin: 0;
  }
  .note {
    -hutu-note-placement: margin;
    -hutu-note-side: inline-start;
    font-size: 0.5em;
  }
</style>

<body>
  <p>正文正文被注文字<span class="note">眉批：此處當作「長卿」。</span>正文正文</p>
</body>
```
