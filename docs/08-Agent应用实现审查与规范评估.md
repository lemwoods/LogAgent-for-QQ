# Agent 应用实现审查与规范评估

> 审查对象：`linux` 分支，HEAD `e4e97e1` + 工作树未提交改动（浏览器截图特性）
> 审查方式：静态阅读（Core / Routing / Skills / Memory / Knowledge / Sandbox / CI / 测试清单）
> 局限：本机无 .NET SDK，**未执行构建与测试**；构建是否通过、测试是否全绿以 CI 为唯一事实来源
> 日期：2026-10-25

---

## 1. 结论

**实现程度约 80%**：M1 连接、M4 知识库基本落地；M2/M3 主体完成，但规划里写明的 guardrails 和 L2 记忆没有兑现；M5 的核心验收项「评估集回归」缺失。

**规范合理性：文档层强、强制层弱。** `AGENTS.md` 与 `docs/02` 的质量在个人项目中属于上游水平，但**没有一条关键约束被自动化**——没有 `.editorconfig`、没有 `Directory.Build.props`、没有 warnings-as-errors、没有 evals、没有 lint 门禁。规范写得比执行得好，这是当前最结构性的问题。

三点主因：

1. 规范是「给 Agent 看的提示词」，不是「编译器能检查的规则」，遵守率随模型/会话波动；
2. 质量反馈靠肉眼，没有评估集，无法回答「这次改动是变好还是变差」；
3. 规划文档与代码已经漂移（MAF/SK、L2），文档不再是可信事实源。

---

## 2. 实现程度：按规划里程碑对表

| 里程碑 | 规划要求（`docs/02` §6） | 实际实现 | 完成度 |
|---|---|---|---|
| **M1 连接** | OneBot11 反向 WS + 官方 Bot + NapCat 托管/扫码 | `ChannelManager` 统一频道抽象，NapCat 进程托管 587 行 | ~95% |
| **M2 核心** | ReAct + 滚动摘要 + **L0/L1/L2** + 路由熔断 | 工具循环、L0 原文 + L1 摘要、熔断都有；**L2 结构化关键事实没有** | ~75% |
| **M3 分析** | Plan-Execute + Reflection + LogTools | 文本模式五步（本地统计→降采样→案例对照→根因→自检）完整；文件模式 7 轮工具循环 | ~85% |
| **M4 知识** | 混合检索 + 审核门 + 双路学习 | FTS5 + LIKE 兜底 + 向量 + RRF；`candidate→approved` 审核门；学习 Agent 双路径 | ~90% |
| **M5 打磨** | 导出 + **评估集回归** + 打包 | 导出（脱敏/DPO/质量标签）、打包、双壳均完成；**评估集 ✗** | ~55% |

超出规划的投入：WPF（7787 行）+ Avalonia（1470 行）+ Headless（872 行）三壳共用同一份 `AgentHost`（`src/LogAgent.Core/AgentHost.cs:12`），这个分层是对的，也让「同一条处理管线」这句承诺可验证。

---

## 3. 缺口清单（按严重度）

### P0-1 没有评估集

> 状态：已实施（2026-10-26），Tier 1 首批 7 条样例 + 行号证据断言，见 `docs/09` §6。

`docs/02:278` 把 M5 验收标准写成「评估集通过率达标」，`docs/01:82` 也写了「从第一天就建结构化测试集，否则迭代全靠肉眼」。但全仓无 eval 实现。

现有 266 个测试属性测的是**组件契约**（解析器、路由、沙箱路径、导出格式），没有一个测 **agent 任务成功率**——即「给一份真实崩溃日志，能否定位到正确的 mod / 版本冲突 / 内存不足」。

后果：提示词、模型、温度的任何调整都无法量化比较，只能靠人肉试。

### P0-2 并发没有串行化

> 状态：已实施（2026-10-26），每 chat 信号量 + 会话原子创建，见 `docs/09` §6。

- `src/LogAgent.App/Services/AppHost.cs:320` 与 `src/LogAgent.Desktop/ViewModels/ShellViewModel.cs:221` 都是 `async void OnChannelMessage`，直接 `await Runtime.OnMessageAsync(...)`；
- `src/LogAgent.Connections/OneBot11Connection.cs:282` 同步 `Invoke` 事件；
- 同一会话的 `ActiveSession.Window` 是普通 `List<ChatTurn>`（`src/LogAgent.Memory/SessionManager.cs:31`），无锁。

群消息并发到达时，同一 chat 的窗口读写、L1 摘要生成、`quality_tag` 记录都可能交错。数据库侧做了 WAL + `busy_timeout`（`src/LogAgent.Memory/Database.cs:27`），内存态没有对应保护。

### P1-1 Guardrails 只兑现了 1/4

> 状态（2026-10-28）：**3/4 完成**——迭代上限、耗时上限、token 预算已实现；敏感操作人工确认（HITL）未做，需产品决策，见 `docs/09` §6.10。

`docs/02:90` 要求「迭代上限（8 步）、token 预算、耗时上限、敏感操作人工确认」。实际只有迭代上限：`src/LogAgent.Core/Agents.cs:28`（6 轮）、`src/LogAgent.Core/LogAnalysisAgent.cs:27`（7 轮）。token 预算、耗时上限、HITL 均无。`run_shell` 命中白名单即执行（`src/LogAgent.Skills/FileTools.cs:223`），唯一防线是白名单本身。

### P1-2 工具异常会掀翻整轮推理

> 状态（2026-10-28）：**已修复**——工具异常转为 observation 文本，见 `docs/09` §6.10。

`SkillRegistry` 内部统一 catch 并返回错误串（`src/LogAgent.Skills/SkillRegistry.cs:251`），但 `ToolHostHub.ExecuteToolAsync` 对 MCP / FileTools / WebTools 三个分支没有统一包裹（`src/LogAgent.Skills/ToolHostHub.cs:50`）。任一分支抛出，异常会穿过 `GroupContextAgent.ReplyAsync` 冒到 `src/LogAgent.Core/AgentRuntime.cs:89` 的兜底，用户收到「内部打了个嗝」，本轮已完成的推理与工具结果全部丢失。

ReAct 的正确语义是「工具失败 = 一条 observation」，不是终止。

### P1-3 规范不可执行

> 状态（2026-10-28）：**第二步完成**——`Directory.Build.props` + `.editorconfig` 就位，`src` 已开启 `TreatWarningsAsErrors`（WPF/测试暂除外）；CI 格式门与测试工程收口待后续，见 `docs/09` §6.10。

`AGENTS.md` 要求主构造函数、collection expression、中文注释、file-scoped namespace，但仓库**没有任何强制机制**：

- 无 `.editorconfig`
- 无 `Directory.Build.props`（15 个 csproj 各自配置 `Nullable`）
- 无 `Directory.Packages.props`（版本散落在 15 个文件）
- 无 `TreatWarningsAsErrors`

结论：这些条款目前等价于提示词。换模型、换会话，遵守率会漂移。

### P2 级欠账

| 项 | 证据 | 影响 |
|---|---|---|
| L2 关键事实未实现 | 仅 `src/LogAgent.Shared/Configuration/Models.cs:17` 注释提到 L1/L2 | 跨会话偏好/结论无法结构化沉淀 |
| Embedding 无故障切换 | `src/LogAgent.Routing/ModelRouter.cs:117` 只取 `chain[0]` | Embedding provider 挂了直接退回纯关键词，用户无感知 |
| 配置明文 + 无权限 | `AppConfigStore.Save`（`src/LogAgent.Shared/Configuration/AppConfigStore.cs:61`）未设文件模式，`ApiKey` 明文 | Linux 桌面 `~/.config` 为 755 时 `config.json` 即 644 |
| 文档与代码漂移 | `docs/02:87` 写 MAF + Semantic Kernel，实际是手写 Router + OpenAI 兼容客户端；无一处引用 MAF/SK | 后来者被误导 |
| 规划中的时间窗切片未实现 | `LogTools` 无时间窗相关代码 | 大日志按时间定位不可用 |

---

## 4. 规范合理性评估

### 4.1 四层护栏模型对照

| 层 | 现状 | 评价 |
|---|---|---|
| ① 人类设计意图 | `docs/01` 学习笔记 + `docs/02` 规划 | **强**，但已与代码漂移 |
| ② Agent 同步文档 | `AGENTS.md` | **强**：构建命令、平台约束、已知环境限制、常见任务入口都写清了，是仓库最有价值的规范资产 |
| ③ 硬性自动约束 | 266 个测试 + 双平台 CI | **薄**：只覆盖「代码对不对」，不覆盖「架构有没有被破坏」和「agent 好不好」 |
| ④ 人工评审回路 | 无 `GOLDEN_RULES.md` | **缺**：没有「事故 → 规则 → 检查」闭环 |

缺的不是第 1、2 层，而是第 3、4 层的机械化。

### 4.2 具体问题

**（1）规范承诺 > 测试现实**

`AGENTS.md:110` 写「Windows 白/黑名单必须与 v0.7.0 逐条一致（**有测试守护**）」。实际：

- `tests/LogAgent.Tests/SandboxTests.cs` 只有 5 个用例，全在测路径守卫（`EnsureInside` / `IsInside` / 工作区创建），**一条白名单都没测**；
- 白名单断言在 `tests/LogAgent.Tests/M3Tests.cs:35-90`，是 13 条抽样命令（`findstr`、`sudo`、`curl | sh`、`mkfs` 等），不是「逐条」。

规范这样写会让后来者误以为有全量守护，改动白名单时放松警惕。要么补全量断言（默认白/黑名单逐条 `InlineData`），要么把措辞改为「有抽样测试守护，改动需自查」。

**（2）规则被自己破例，且没有拦截**

规则要求「不要在各模块里写 `OperatingSystem.IsWindows()`，统一走 `PlatformShell` / `PlatformInfo`」。违反处：

- `src/LogAgent.App/Pages/NapCatViewModel.cs:140`：UI 文件对话框过滤器直接写平台分支；
- `src/LogAgent.Desktop/Services/PlatformService.cs`：整文件在做平台分支（可解释为壳层职责，但规则没写这个例外）。

合法例外有两处，注释已说明理由（CA1416 平台流分析）：`src/LogAgent.Mcp/McpTransport.cs:171`、`src/LogAgent.NapCat/NapCatManager.cs:155`。

问题不在违规本身，而在于没有 lint 能发现它——规则只能靠人肉 review。

**（3）测试命名按里程碑**

`M3Tests` / `M4Tests` / `M5Tests` 按开发阶段命名，内部混装不相关行为（`M5Tests` 同时测脱敏、训练导出、NapCat 反向 WS 模板、模型列表解析）。按行为域命名才能回答「改动 X 该跑哪组」。

**（4）工作树里有临时产物**

`tests/LogAgent.Tests/TempHarnessMain.cs` 处于未跟踪状态。临时验证脚本不应留在测试工程内，容易误提交并进入 CI 构建。

### 4.3 `AGENTS.md` 逐节体检

| 章节 | 准确性 | 可执行性 | 建议 |
|---|---|---|---|
| §2 构建与验证 | ✅ 命令与 slnx 一致 | ✅ | 保持 |
| §3 架构要点 | ⚠️ 缺 `Desktop`/`Android` 在依赖图中的位置 | ⚠️ 靠人读 | 补一张三层壳的依赖说明 |
| §4 代码风格 | ✅ 与现状相符（37 处主构造函数） | ❌ 无编译器约束 | 落 `.editorconfig` + `TreatWarningsAsErrors` |
| §5 提交前自查 | ✅ 三条事故真实 | ❌ 全靠自觉 | 转化为 `GOLDEN_RULES.md` + 结构检查 |
| §6 测试约定 | ✅ 平台属性确实存在（`PlatformTestAttributes.cs`） | ✅ | 补「测试命名按行为域」一条 |
| §7 安全边界 | ✅ | ❌ 无检查 | 至少加 `config.json` 权限与密钥扫描 |
| §9 已知环境限制 | ✅ 与本次审查环境一致 | — | 保持 |
| §10 常见任务入口 | ⚠️ 缺新模块（Browser / Desktop） | ⚠️ | 与代码同步 |

---

## 5. 修复路线

| 优先级 | 动作 | 验收方式 |
|---|---|---|
| **P0** | 建最小评估集（20 条日志 → 关键结论断言） | `dotnet test` 新增一组用例全绿；改动提示词后能看出通过率变化 |
| **P0** | 按 chatId 串行化消息处理 | 并发投递同一 chat 的多条消息，断言窗口顺序与摘要不串 |
| P1 | `ToolHostHub` 统一 try/catch，工具异常转 observation | 用一个必抛的工具，断言本轮仍能产出回答 |
| P1 | `Directory.Build.props` + `.editorconfig` + warnings-as-errors | 构建期拦截风格与可空性问题 |
| P1 | 修正 `AGENTS.md:110` 措辞，或补全量白名单断言 | 二者取其一，规范与测试对齐 |
| P1 | 补 token 预算与耗时上限 | 超预算时降级/中止并可观测 |
| P2 | L2 关键事实、Embedding 熔断、`config.json` 0600、清理临时文件、同步文档漂移 | 逐项在 `docs/02` 勾掉 |

---

## 6. 审查局限

- 未执行构建与测试（无 .NET SDK），「能否编译」「测试是否全绿」未经本地验证；
- `Connections` / `NapCat` / `Mcp` / `Training` 只读了接口与关键路径，未逐行审计；
- CI 的实际执行结果未获取，`ci.yml` 只做了配置层检查；
- 完成度百分比是**规划项覆盖度**的粗略估计，不是代码质量分数。
