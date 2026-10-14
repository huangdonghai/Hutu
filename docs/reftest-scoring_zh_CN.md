# 借用语料的检测：快速检测与全量检测

这篇文档记录 `conformance/reftests/wpt` 和 `conformance/reftests/gecko` 下这批**借用语料**怎么用。
写在这里是因为用法全在环境变量和命令行参数里，不看文档想不起来。

---

## 1. 先搞清楚有两套语料，别混

| | 位置 | 谁跑 | 断言？ |
|---|---|---|---|
| **手写语料** | `conformance/reftests/**`（`wpt/`、`gecko/` 之外） | `ReftestTests` | **硬断言**，逐像素，红就是红 |
| **借用语料** | `conformance/reftests/wpt`、`conformance/reftests/gecko` | `ImportedCorpusTests` | **只记分，不断言** |

**为什么借用语料不能硬断言**：它是从上游整套搬来的，一万多对，绝大多数是关于引擎还没实现的特性。
从第一天起就会红，而且导入越多越红——没人会读第 80 个失败，第 79 个也就不重要了。
所以它的价值是**那个数字**：唯一一个没人挑选过用例的引擎度量。

**为什么手写语料不含文字**：它是逐像素精确比较，文字宽度随机器字体变化。
借用语料无所谓——一对页面由同一个引擎、同一套字体画两遍再互相比，字体在比较里抵消掉了。

判断"哪些是借用的"靠 `PROVENANCE.md`：哪个目录下有这个文件，`ReftestTests` 就把整个目录排除。
所以**手动往 `wpt/`、`gecko/` 里加文件是不会被断言的**，只会进记分。

---

## 2. 当前规模

| 源 | 对数 | 上游 |
|---|---|---|
| WPT `css/` | 10152 | commit `2f57f45d21482f94b36123dfcec77ae7ab859bc1` |
| Gecko `layout/reftests` | 3815 | commit `5836a062726f715fda621338a17b51aff30d0a8c` |
| 合计 | 约 13900 | 约 2.8 万个 HTML 文件 |

明细见 `build/import-report-wpt.md` / `build/import-report-gecko.md`。

---

## 3. 快速检测（修引擎时用这个）

**用途**：画到攒够 N 个 finding 就停。几秒出结果，每一条都指向一个可以打开的页面。

```powershell
cd d:/_git0/Hutu
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj `
  --filter "FullyQualifiedName~StopsAtTheTenthFinding" --no-build
```

**产出**：

| 文件 | 内容 |
|---|---|
| `build/reftest-quick.md` | finding 列表：相对路径 + 差异像素数 + 首个差异坐标与两边颜色 |
| `build/quick.log` | 进度日志 |

**行为说明**：
- 并行画，攒够 10 个就停止**调度**；在飞的页面还会画完，所以实际通常比 10 多几个（首次跑出 18 个）。
- 报告第一行是 `stopped after drawing N pages, with M to look at`——N 是真实画过的页数，
  M 个 finding 是 **M/N** 而不是 M/全量，别误读成比例。
- `HUTU_SCORE_FINDINGS` 改停止阈值，默认 10。

> **坑：快速检测的 finding 列表是样本，不是全集。** 它只画语料**队首**（按路径排序）约三十页就停，
> 而且并行下"在飞"的页数不固定，所以每轮画到第几页、列出几条都会有波动。
> **不要把两轮之间的列表差异当成回归**——曾经因此误判过：某条 finding 这轮有、下轮没有，其实它一直在失败，
> 只是这轮没画到那里。
>
> 要可靠对比，用**固定样本**：全量检测加 `HUTU_SCORE_LIMIT=N`（每个语料只画前 N 页，确定性），
> 或者直接跑全量。要确认某一条到底修没修，用 `HUTU_PROBE_PAGE` 看布局树，那是一对一的确定答案。

---

## 4. 全量检测（一天结束时看数字）

```powershell
cd d:/_git0/Hutu
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj `
  --filter "FullyQualifiedName~TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay" --no-build
```

**产出**：

| 文件 | 内容 |
|---|---|
| `build/reftest-score.md` | 每个语料的通过率 + "已经画对的"清单 + finding 清单 |
| `build/score-wpt.log`、`build/score-gecko.log` | 进度日志 |

**环境变量**：

| 变量 | 默认 | 作用 |
|---|---|---|
| `HUTU_SCORE_LIMIT` | 0（不限） | 每个语料最多画几页。**冒烟用，也是唯一可靠的回归对比基线**，见下 |
| `HUTU_SCORE_FINDINGS` | 10 | 只影响快速检测 |

### 回归对比：用固定样本，别用快速检测

判断"这次改动是修好了还是弄坏了"，要用**确定性样本**：

```powershell
$env:HUTU_SCORE_LIMIT='60'
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj `
  --filter 'FullyQualifiedName~TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay' --no-build
```

它画每个语料的前 60 页，**结果每次完全一致**（不依赖并行时序），6 秒跑完，
比快速检测只慢 3 秒却多覆盖 4 倍页面。日常迭代建议用它，快速检测留给"随手看十条"。

**当前基线**（2026-10-14，13,928 对语料的队首 120 页）：

```
gecko: 60 pairs — 48 通过 (80%)，12 不一致
wpt:  60 pairs — 21 通过 (35%)，39 不一致
合计: 120 pairs — 69 通过 (57.5%)
```

改完引擎跑一遍：**通过数往上走就是修好了，往下走就是弄坏了**。看通过数而不是 finding 条数——
条数会随提前停止而漂移（见上一节那个坑），通过数不会。

**两个自动刹车**（都是为了后台跑的安全）：
- 单页绘制超时 **5 秒**（`ReftestRunner.Limit`）。实测均值约 0.11 秒/页，5 秒已是正常值 50 倍。
- 累计 **8 个**页面没回来就停跑，报告里会说明。引擎"画不完"本身是一个发现，
  记分承载不了它，不值得为它烧完整台机器。

> 全量耗时是估计值（约 10 分钟量级，随机器核数变化），没有在最新这批语料上实测过。

---

## 5. 后台跑（重要，否则界面会一直等）

工具调用是**同步**的：我发起一条命令，对话这一轮要等命令结束才返回。进程在 OS 层面是独立的，
但对话被它绑着——所以前台跑一个长任务，界面就一直在转圈。

而且 `| Select-Object -Last N` 会把输出**全部缓冲**，`dotnet test` 的进度一行都不流回来，
看起来和卡死没有区别。

**正确做法是启动后立刻返回，然后 poll 日志**：

```powershell
# 启动，立刻返回
Start-Process dotnet -ArgumentList `
  'test','src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj',
  '--filter','FullyQualifiedName~TheEngineDrawsSomeOfTheBorrowedPagesTheWayTheySay','--no-build' `
  -RedirectStandardOutput build/score.out -WindowStyle Hidden

# 随时瞄一眼，最后一行就是进度
Get-Content build/score-wpt.log -Tail 3
Get-Content build/score-gecko.log -Tail 3
```

**日志怎么读**（每 100 个一行，带时间戳）：

```
08:48:09 begin, 13928 to do
08:48:10 100
08:48:11 200
08:48:12 done, 32
```

- 最后一行数字在涨 = 在干活
- 最后一行时间戳不动了 = 停了（跑完就是 `done`，没跑完就是卡住）
- **这就是"慢"和"卡"唯一能从外部区分的办法**，也是为什么要写这个日志

---

## 6. 把记分变成护栏（提升）

借用语料里画对的 pair，可以提升成手写语料那样被硬断言：

1. 从 `build/reftest-score.md` 的 "What the engine already gets right" 里挑一对；
2. 把 `conformance/reftests/wpt/xxx/foo.html` 和 `foo-ref.html` 一起移到
   `conformance/reftests/xxx/`（**不要**留在 `wpt/` 下，`PROVENANCE.md` 会让它继续只记分）；
3. 不用改任何代码，`ReftestTests` 自动接管。

**限制：目前只能提升不含文字的 pair。** 带文字的 pair 一旦硬断言，像素比较就会随机器字体变化。
要做这件事需要"固定字体度量 + 可确定性绘制"的渲染路径，而 `EngineReader.WithFixedMetrics`
现在**不能渲染**（`Render` 会抛 `a reftest is drawn with real typefaces`），它只服务于布局 golden。

---

## 7. 重新导入

导入是**幂等覆盖写**，可以随便重跑。`build/` 被清掉的话，先重新拉源码：

```powershell
git clone --depth 1 --filter=blob:none --sparse https://github.com/web-platform-tests/wpt.git build/wpt
git -C build/wpt sparse-checkout set css
git clone --depth 1 --filter=blob:none --sparse https://github.com/mozilla/gecko-dev.git build/gecko
git -C build/gecko sparse-checkout set layout/reftests
```

然后（`build/` 在 `.gitignore` 里，不污染仓库）：

```powershell
# 只统计，不落盘
$env:HUTU_IMPORT_SOURCE="d:/_git0/Hutu/build/wpt/css"; $env:HUTU_IMPORT_KIND="wpt"; $env:HUTU_IMPORT_PHASE="filter"
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj --filter "FullyQualifiedName~CorpusImporter" --no-build

# 真导入
$env:HUTU_IMPORT_PHASE="run"
$env:HUTU_IMPORT_COMMIT="<commit hash>"
dotnet test src/Hutu.Conformance.Tests/Hutu.Conformance.Tests.csproj --filter "FullyQualifiedName~CorpusImporter" --no-build
```

| 变量 | 说明 |
|---|---|
| `HUTU_IMPORT_SOURCE` | 必填，WPT 的 `css` 目录或 Gecko 的 `layout/reftests` |
| `HUTU_IMPORT_KIND` | `wpt` 或 `gecko`，决定用哪套发现逻辑 |
| `HUTU_IMPORT_PHASE` | `filter` 只统计，`run` 才落盘 |
| `HUTU_IMPORT_LIMIT` | 冒烟用的条数上限 |
| `HUTU_IMPORT_COMMIT` | 写进 `PROVENANCE.md` 的出处，**导入时一定要填** |

Gecko 把 `HUTU_IMPORT_KIND` 改成 `gecko`、`HUTU_IMPORT_SOURCE` 指向 `build/gecko/layout/reftests` 即可。

**导入保留什么、排除什么**：保留静态布局（**含文字**）；排除带脚本 / `<iframe>` / `<canvas>` /
`<svg>` / `<img>` / `url(...)` / 动画的页面，以及依赖窗口尺寸（`vw`/`vh`/带 width 的 `@media`）的页面。
被排除的按原因分类计数，写在 `build/import-report-*.md` 里——其中 `something fetched by url`
这一类是"资产没一起拷过来"，属于值得回头看的一批，不是真的不能要。

---

## 8. 排错

| 现象 | 原因 |
|---|---|
| 界面一直转圈 | 前台跑了长任务，且输出被 `Select-Object` 缓冲了。改用第 5 节的后台方式 |
| 日志时间戳不动了 | 卡住。全量/快速都有 5 秒单页超时和 8 次超时预算，理论上不该发生 |
| `TimeoutException: drawing xxx was still going after 5 seconds` | **引擎缺陷**：该页面布局不终止。这是语料的价值所在 |
| `the page produced no paint list to draw` | **引擎缺陷**：页面没产出绘制列表 |
| `ArgumentOutOfRangeException` | **引擎缺陷**：布局中越界（已知：`css-gaps/grid/subgrid/**` 一批） |
| `declares rel="match" with no href` | 页面的 `href=` 没写引号，正则只认带引号的值。导入器已修（无引号也会被改写），重导即可；若还有漏网，看 `ImportedCorpusTests.Value` 的正则 |
| `dotnet test` 跑出几千个失败 | 语料被放到 `wpt/`、`gecko/` 外面了，或者 `PROVENANCE.md` 被删了——借用语料回到硬断言了 |

---

## 9. 相关代码

| 文件 | 职责 |
|---|---|
| `src/Hutu.Conformance.Tests/ImportedCorpusTests.cs` | 记分 fixture：全量 + 快速两条路 |
| `src/Hutu.Conformance.Tests/ReftestTests.cs` | 手写语料的硬断言；按 `PROVENANCE.md` 排除借用语料 |
| `src/Hutu.Conformance.Tests/ReftestRunner.cs` | 绘制 + 逐像素比较 + 5 秒超时 |
| `src/Hutu.Conformance.Tests/Progress.cs` | 进度日志（`build/<run>.log`） |
| `src/Hutu.Conformance.Tests/CorpusImport/CorpusImporter.cs` | 导入：过滤 + 拷贝 + 报告 |
| `src/Hutu.Conformance.Tests/CorpusImport/ImportDiscovery.cs` | WPT / Gecko 各自的配对发现 |
| `src/Hutu.Conformance.Tests/CorpusImport/ImportFilters.cs` | 静态 / 窗口无关的过滤规则 |
