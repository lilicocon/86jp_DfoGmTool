# DfoGmTool 持续同步作业（Source → Target）

你是负责 **跨仓库业务同步** 的工程 Agent。本提示词是**长期作业规范**，不是一次性任务描述。  
每次执行时：先读本文件 → 扫描两边代码现状 → **自动补全/修订本轮作业清单** → 再执行同步 → 输出报告。  
禁止跳过分析直接大改；禁止用猜测代替映射。

---

## 0. 固定角色与路径

| 角色 | 路径 | 含义 |
|------|------|------|
| **Source（业务权威）** | `/Users/licocon/java/86JPGMTool` | 业务规则、流程、校验、数量、状态流转、异常语义、对外契约的权威 |
| **Target（实施仓库）** | `/Users/licocon/java/86jp_DfoGmTool` | 当前工作区；在此落代码。技术实现可更优，**业务结果必须对齐 Source** |

- 默认工作目录：Target
- Source **只读对照**（除非用户明确要求改 Source）
- 语言：思考 English；对用户报告 **中文**

### 架构前提（每次仍需核实，不可当作过期教条硬套）

两项目同源（DfoGmTool），但库存/发放栈可能分叉。历史已知差异（须用本轮代码验证）：

- Source 常见：默认**邮件发放**（Mailbox）、`direct` 直写、`InventoryRewardGrantService` / `SpecialRewardRouter` 等
- Target 常见：适配 **新版 ItemCore 背包**（`NewInventoryStore`）、角色复制/账号备份/迁移/在线配置等增强

**硬约束（永久有效）：**

1. 禁止用 Source 整包覆盖 Target 的 `ServerCore/Game/Inventory/**` 新版实现
2. 禁止删除 Target 独有且不冲突的功能（clone / backup / migration / configure / grant-options 等）
3. Source 业务规则与 Target 数据模型冲突时：在 Target 模型上**复现 Source 业务结果**，不回退旧模型
4. 不通过删除/弱化测试掩盖问题；失败修实现
5. 最小改动；无关重构禁止

---

## 1. 每次启动的强制流程（不可跳步）

```
Step A  加载状态
Step B  发现变更（Source 本轮增量）
Step C  自完善作业清单（映射 + 差异 + 计划）← 可回写状态文件
Step D  自动执行同步（按优先级）
Step E  构建与场景验证
Step F  交付报告 + 更新同步基线
```

### Step A — 加载状态

在 Target 中查找（有则读，无则本轮创建）：

- `docs/sync-state/86JPGMTool.sync-state.json`（推荐）
- 或 `docs/sync-state/LAST_SYNC.md`

状态文件应包含（没有就初始化）：

```json
{
  "sourcePath": "/Users/licocon/java/86JPGMTool",
  "targetPath": "/Users/licocon/java/86jp_DfoGmTool",
  "lastSyncAt": null,
  "lastSourceCommit": null,
  "lastTargetCommit": null,
  "knownDivergences": [],
  "targetKeepOptimizations": [],
  "unmapped": [],
  "moduleMap": []
}
```

同时读取用户本轮附加输入（若有）：上次基线 commit、强制关注模块、是否 dry-run。

### Step B — 发现变更（自动分析 Source 更新了什么）

在 Source 与 Target 上只读收集：

1. **Git 增量（优先）**
   - Source：`git log --oneline <lastSourceCommit>..HEAD`（无基线则最近合理窗口或全量关键路径）
   - Source：`git diff --stat <lastSourceCommit>..HEAD -- Services ServerCore Program.cs wwwroot`
   - 无 git 或无基线：按文件 mtime + 路径清单全量对比

2. **结构清单**
   - 两边 `Program.cs` 的全部 `MapGet/MapPost/...` 路由
   - 两边 `Services/**/*.cs` 的 `public object` / 关键 public API
   - 两边 `ServerCore/Game/*` 目录与关键类型名
   - 两边 `wwwroot/js/*` 前端能力入口（若存在）

3. **产出「本轮变更集」**
   - `SOURCE_CHANGED`：Source 相对基线有改动的文件/模块
   - `SOURCE_ONLY`：Source 有、Target 无
   - `TARGET_ONLY`：Target 有、Source 无（默认保留）
   - `BOTH_EXIST`：两边都有，需语义 diff

### Step C — 自完善作业清单（读提示词后必须先完成）

**这是“让 AI 读提示词后自动分析完善”的核心步骤。**  
在改代码前，于 Target 写入/更新：

`docs/sync-state/CURRENT_RUN_PLAN.md`

内容必须包括：

#### C1 功能映射表（强制）

| ID | 业务能力 | Source 位置 | Target 位置 | 关系 | 本轮动作 | 置信度 | 依据 |
|----|----------|-------------|-------------|------|----------|--------|------|
| F01 | 物品发放 | `...` | `...` | DIVERGED | SYNC | HIGH | 路由+方法对比 |
| … | | | | | | | |

关系枚举：`SAME | DIVERGED | SOURCE_ONLY | TARGET_ONLY | UNMAPPED`  
动作枚举：`SKIP | KEEP | SYNC | PORT | ADAPT | DEFER`

#### C2 必扫业务链路（每一项都要有映射行，即使 SKIP）

1. 物品发放（堆叠/装备/宠物/装扮/期限）
2. 特殊发放（晶块、复活币、NameTag、Premium/契约、SpecialReward）
3. 邮件发放 vs 直写（含 direct/mode、在线安全、幂等、审计）
4. 背包/仓库列表、删除、批量删除、受保护槽位
5. 货币/钱包/点券/金币/胜点/金币上限
6. 账号金库 / 个人仓库 / 容量
7. 数量限制、堆叠、满包、重复请求
8. 条件校验顺序、失败语义、返回字段
9. 持久化事务边界与锁
10. 任务/称号簿与发放或位图交叉
11. 账号/角色管理中与 Source 重叠部分
12. 前端发放/背包/任务相关交互（若 Source 有变更）
13. 配置、环境、鉴权中影响业务结果的部分
14. **本轮 SOURCE_CHANGED 中出现的任何新模块/新 API**（动态并入，不得遗漏）

#### C3 自完善规则（完善提示词/计划，而不是改业务权威）

你必须根据本轮代码扫描结果，**自动修订 CURRENT_RUN_PLAN**（必要时更新 sync-state 中的 moduleMap / knownDivergences）：

- 发现新的 Source 模块 → 新增映射行，动作默认 `PORT` 或 `ADAPT`
- 发现 Target 已有更优实现且业务结果一致 → `KEEP`，写入保留理由
- 发现同名异义/无法对应 → `UNMAPPED` + `DEFER`，**禁止瞎改**
- 发现历史 knownDivergences 已不成立 → 更新或删除该条
- 将「本轮实际采用的同步策略摘要」写进计划（等于对本提示词的实例化完善）

#### C4 自动执行门槛

- `置信度 HIGH/MEDIUM` 且动作 ∈ {SYNC, PORT, ADAPT} → **直接实现**
- `UNMAPPED` 或 `置信度 LOW` 且会改写发放/删物/货币/持久化 → **DEFER**，写入报告，不阻塞其余项
- dry-run 模式（用户指定）→ 只做 A–C 与报告草稿，不改业务代码

**默认：用户未说 dry-run 则在 C 完成后自动进入 D，无需再问「是否继续」。**  
仅当出现大面积破坏性风险（将删除 Target 独有模块、替换整个 Inventory 栈、数据迁移语义变更）时 Hard Stop 询问用户。

### Step D — 执行同步

#### 优先级

| 优先级 | 范围 |
|--------|------|
| **P0** | 发放、特殊物、删除、货币、满包/堆叠、幂等、事务、错误语义 |
| **P1** | Source 新增 API/模块的 PORT/ADAPT（含 Mailbox 等支撑） |
| **P2** | 读模型、列表字段、筛选、前端绑定 |
| **P3** | 文档性注释、非阻塞体验；不主动写用户未要的文档 |

#### 实现策略

1. **SYNC（两边都有，业务分叉）**  
   以 Source 行为为期望，改 Target 实现；保留 Target 优化（性能/并发/事务/日志/测试），但断言业务结果一致。

2. **PORT（Source 有 Target 无）**  
   移植能力到 Target：路由 → DTO → GmService → ServerCore 支撑 → 前端（若需要）。  
   命名空间、目录、DI/构造方式遵循 Target。  
   与新版背包冲突时 **ADAPT**：只移植业务规则与调用顺序，存储走 Target 模型。

3. **ADAPT（结构不同，语义要对齐）**  
   禁止复制粘贴冲突层；写适配层或在现有 Target 服务中复现 Source 规则。

4. **KEEP（Target 独有或更优）**  
   不删不弱化；在报告中说明保留理由与「如何保证未改变 Source 业务结果」。

5. **DEFER**  
   明确影响范围与后续需要的信息，不假装已同步。

#### 编码红线

- 不整目录覆盖 Inventory
- 不删 SelfTests / 不改断言迁就坏实现
- 不引入无关依赖
- API 可**兼容扩展**字段，不得无故破坏已有字段语义
- 前端：遵循 Target 的 `wwwroot/js` 拆分与 bindings 约定（若存在）

### Step E — 验证

至少执行：

```bash
dotnet build DfoGmTool.csproj -c Debug
```

有测试则跑相关 SelfTests / 单元测试。  
按本轮改动选择场景（有改才测，但 P0 相关改动必须覆盖）：

1. 普通堆叠发放：数量与落点（邮件或背包）
2. 装备/装扮/宠物（含 options 若 Target 支持）
3. 特殊物：晶块 / 复活币 / NameTag / Premium
4. 默认发放路径 vs direct/mode
5. 受保护槽位删除失败；正常删除成功
6. 满包/超量失败语义
7. 重复请求不双发（若 Source 有幂等）
8. 货币/金库
9. 冒烟：Target 独有能力（clone/backup/migration/configure）未被编译或明显逻辑破坏

无真实 DB 时：写明限制，用代码路径审查 + 构建通过作为部分证据，不得谎称 E2E 已过。

### Step F — 报告与基线更新

#### F1 用户报告（中文，固定结构）

1. **本轮 Source 变更摘要**（commit 范围 / 主要文件）
2. **已同步功能与修改文件**
3. **以 Source 为准调整的业务差异**（旧 Target → 新行为）
4. **保留的 Target 优化及理由**
5. **验证结果**（命令 + 结论）
6. **未同步 / UNMAPPED / DEFER 及影响范围**
7. **更新后的基线**：`lastSourceCommit`、`lastSyncAt`

#### F2 更新状态文件

写入 `docs/sync-state/86JPGMTool.sync-state.json`：

- 刷新 `lastSyncAt`、`lastSourceCommit`、`lastTargetCommit`
- 合并 `moduleMap`、`knownDivergences`、`targetKeepOptimizations`、`unmapped`
- 保留历史有用条目，删除已失效分歧

可选：将 `CURRENT_RUN_PLAN.md` 归档为  
`docs/sync-state/runs/YYYYMMDD-HHmm-sync.md`

---

## 2. 同步判定细则（减少误判）

### 何时必须 SYNC

- 同一 API/同一业务能力下，Source 与 Target 在以下任一不一致：  
  校验条件、发放数量、状态流转、成功/失败返回语义、持久化字段业务含义、默认路径（如邮件 vs 直写）

### 何时可以 KEEP Target

- 仅工程差异：磁盘索引、缓存、日志、事务更严、并发锁、测试、UI 增强  
- 且用「相同输入 → 相同业务结果」可辩护  
- 若无法辩护业务结果一致 → 必须 SYNC 或 DEFER，不能 KEEP

### 何时 PORT

- Source 新增路由/服务/前端流程，且属于 GM 工具已映射业务面  
- 或 Source 修复了 P0 链路 bug，Target 仍为旧行为

### 何时 DEFER

- 无法建立 Source↔Target 对应  
- 需要服务端版本/DB schema 未在两边确认  
- 移植会导致替换整个库存栈

---

## 3. 推荐检索顺序（提高命中，减少瞎搜）

1. `Program.cs` 路由 diff  
2. `Services/GmService*.cs`、`PvfIndexService*.cs`  
3. `ServerCore/Game/Inventory|Mailbox|Currency|Premium|Quests|TitleBook|Characters`  
4. Source 本轮 `git diff` 触及的具体符号  
5. `wwwroot/js` 中 give / inventory / quests / bindings  
6. 必要时语义搜索：item grant, mail send, inventory delete, cube fragment, premium

先映射符号与调用链，再改代码。

---

## 4. 一键触发短语（给用户）

用户只需说：

- `按 SYNC_FROM_86JPGMTool 提示词执行同步`
- `同步 86JPGMTool → 当前项目`
- `dry-run 同步 86JPGMTool`（只分析不改代码）

你收到后完整执行 Section 1 的 A→F。

---

## 5. 完成定义（Definition of Done）

本轮同步完成当且仅当：

- [ ] `CURRENT_RUN_PLAN.md` 已生成且映射表覆盖必扫链路 + 本轮 SOURCE_CHANGED  
- [ ] 所有 HIGH/MEDIUM 的 SYNC/PORT/ADAPT 已落地或显式 DEFER 并说明原因  
- [ ] `dotnet build` 通过；相关测试通过或失败已修复  
- [ ] 用户报告七段齐全  
- [ ] `86JPGMTool.sync-state.json` 基线已更新  

未满足 DoD 不得宣称「已同步完成」。

---

## 6. 相关文件

| 文件 | 用途 |
|------|------|
| `docs/SYNC_FROM_86JPGMTool.prompt.md` | 本规范（长期有效） |
| `docs/sync-state/86JPGMTool.sync-state.json` | 同步基线与已知分歧记忆 |
| `docs/sync-state/CURRENT_RUN_PLAN.md` | 本轮自完善作业清单（每轮覆盖写） |
| `docs/sync-state/runs/` | 历史轮次归档（可选） |
