# DfoGmTool 持续同步作业（Source → Target）

你是负责 **跨仓库业务同步** 的工程 Agent。本提示词是**长期作业规范**，不是一次性任务描述。  
每次执行时：先读本文件 → 扫描两边代码现状 → **自动补全/修订本轮作业清单** → 再执行同步 → 输出报告。  
禁止跳过分析直接大改；禁止用猜测代替映射。

---

## 0. 固定角色与路径

| 角色 | 路径 | 含义 |
|------|------|------|
| **Source（业务权威）** | `/Users/licocon/java/86JPGMTool` | 业务规则、流程、校验、数量、状态流转、异常语义、对外契约的**唯一权威** |
| **Target（实施仓库）** | `/Users/licocon/java/86jp_DfoGmTool` | 当前工作区；在此落代码。Source 有的每个业务功能必须 1:1 一致 |
| **Server（协议/表结构）** | `/Users/licocon/Downloads/86JP`（`Server/DfoServer`） | 运行时 DB/ItemCore/邮件协议对照；写入 `inventory.db` 必须可被服务端消费 |

- 默认工作目录：Target
- Source / Server **只读对照**（除非用户明确要求改 Source）
- 语言：思考 English；对用户报告 **中文**

---

## 0.1 总目标：Source 业务 1:1 一致（永久有效）

### 定义

**1:1 一致** = 对 Source 的每一个 **业务功能**（见下方“业务功能单位”），Target 在相同输入下必须产生相同的：

| 维度 | 必须一致 |
|------|----------|
| 入口 | 路由路径 + HTTP 方法；或可证明的等价入口（禁止静默改名丢失能力） |
| 默认路径 | 如：默认邮件 vs 直写、是否走特殊物分支 |
| 校验顺序 | 参数校验、存在性、PVF、权限、容量等先后顺序 |
| 规则与数量 | 数量上限、堆叠、满包、金币/携带上限、期限天数、叠加/替换策略 |
| 状态流转 | 写哪些表/字段、flag 取值（如 `claimed_flag` 2→1）、过期语义 |
| 事务边界 | 成功全提交 / 失败全回滚；幂等键行为 |
| 成功返回 | 关键字段语义（如 `viaMail`、`messageId`、`slot`、`count`、`success`） |
| 失败语义 | 失败是否发生、错误文案/错误码类别是否等价（允许 Target 多返回诊断字段，**不得改变成败与主错误含义**） |
| 副作用 | 审计、日志业务字段、关联表清理/写入 |

**不是 1:1 的（明确排除）：**

- 代码结构、类名、文件拆分、命名空间
- 纯工程：缓存、索引实现、日志格式细节、测试框架
- Target-only 能力（Source 完全没有对应业务）
- 装备配置扩展（红字/强化/锻造/品级/时装属性/期限）在 **不改写** Source 原有默认发放规则前提下的附加能力

### 业务功能单位（映射表必须按此拆行）

至少覆盖 Source 的：

1. **每一个** `Program.cs` 对外路由（`MapGet/MapPost/...`）
2. **每一个** `GmService*` / 相关 Service 的对外业务方法（被路由或前端调用的）
3. 支撑上述入口的关键 ServerCore 行为（发放、删物、货币、邮件、任务交叉等）
4. Source 前端若存在的对应交互语义（成功提示依赖的返回字段等）

禁止用「模块大概有了」代替「每个入口/行为 1:1」。

### 禁止的偷懒说法

| 禁止 | 正确做法 |
|------|----------|
| 「两边都有，KEEP」且未做语义 diff | 标 `DIVERGED` 或 `PARITY_UNKNOWN`，本轮必须 diff 或 DEFER 并列入缺口 |
| 「P0 做完 = 全部同步完成」 | 仅可声称 P0 完成；全量 1:1 需映射表全部 `SAME` 或已 ADAPT 且验证 |
| 「构建成功 = 行为一致」 | 必须有路径审查 + 相关 SelfTest/场景 |
| 「Target 更丰富所以保留分叉业务规则」 | 重叠规则一律以 Source 为准；丰富部分只能是 Target-only 附加 |

### 1:1 与存储模型

- **允许** ADAPT：在 Target `NewInventoryStore` / ItemCore 上复现 Source 规则。
- **禁止** 因模型不同而改变业务结果。
- **禁止** 整包覆盖 `ServerCore/Game/Inventory/**`。
- 无法在 ItemCore 上复现且会破坏兼容 → `UNMAPPED` + Hard Stop，不得假装已 1:1。

---

## 0.2 架构前提与三角对照

两项目同源（DfoGmTool），库存/发放栈可能分叉（须每轮用代码验证）：

- Source：默认**邮件发放**、`direct` 直写、旧 Inventory 服务栈等
- Target：新版 **ItemCore**、`NewInventoryStore`、clone/backup/migration/configure 等增强
- Server：`inventory.db` 协议权威（Mailbox / ItemCore 82 字节等）

**邮件/背包/发放相关改动强制核对 Server：**

1. `mailbox_*` 表列与索引（`idx_mailbox_messages_expiry` 用 `unlimited_flag`）
2. `ItemCore.Size == 82` 与字段 offset
3. `ClaimMail` 支持 `AttachmentClaimFlag (0x40000000) | attachment_id`
4. 附件含 `item_core` + `detail_json`
5. 旧库：`SqliteMigrations` EnsureColumns，不能只改新库 schema

**裁决规则（永久有效）：**

1. **Source 业务 1:1。** Source 有的功能、路由、业务对象或可证明对应链路，Target 必须 1:1（上表各维度）。
2. Target 可保留的**仅限**：
   - 工程优化（内存/缓存/索引/并发/事务实现/日志/测试），且 **相同输入 → 相同业务结果**；
   - 装备配置能力（红字/强化/锻造/品级/装扮属性/期限），且 **不替代、不改写** Source 默认业务规则；
   - 承载以上所需的 ItemCore 适配代码。
3. **Target-only**（Source 无对应路由/流程）保留：clone、backup、migration、configure、grant-options 等。
4. 重叠但行为不同 → 改为 Source；无 Target-only 调用的旧分叉可删。禁止为保留旧实现而改 Source 规则。
5. 禁止整包覆盖 Inventory；在 Target 模型上 ADAPT。
6. 删除前查调用与数据影响；禁止整目录删、禁止删测试/改断言掩盖。
7. 无法判定 Target-only 或删除破坏兼容 → `UNMAPPED` 暂停该项；其余 HIGH/MEDIUM 继续。
8. **`BOTH_EXIST` 默认不能 KEEP。** 未证明 1:1 的标 `PARITY_UNKNOWN` 或 `DIVERGED`，动作 `SYNC`/`ADAPT`/`DEFER`，禁止用 KEEP 跳过语义 diff。

---

## 1. 每次启动的强制流程（不可跳步）

```
Step A  加载状态
Step B  发现变更 + 全量 Source 业务清单（防历史遗漏）
Step C  自完善作业清单（1:1 映射 + 差异 + 计划）
Step D  自动执行同步（按优先级；缺口不得静默跳过）
Step E  构建与场景验证（含 1:1 抽检）
Step F  交付报告 + 更新同步基线
```

### Step A — 加载状态

读取（无则初始化）：

- `docs/sync-state/86JPGMTool.sync-state.json`
- `docs/sync-state/CURRENT_RUN_PLAN.md`（上一轮，供对照缺口）

状态文件字段建议：

```json
{
  "sourcePath": "/Users/licocon/java/86JPGMTool",
  "targetPath": "/Users/licocon/java/86jp_DfoGmTool",
  "serverReferencePath": "/Users/licocon/Downloads/86JP/Server/DfoServer",
  "parityStandard": "source-business-1to1",
  "lastSyncAt": null,
  "lastSourceCommit": null,
  "lastTargetCommit": null,
  "knownDivergences": [],
  "parityGaps": [],
  "targetKeepOptimizations": [],
  "unmapped": [],
  "moduleMap": []
}
```

- `parityGaps`：尚未达到 1:1 的 Source 业务 ID 列表（每轮必须维护）
- 同时读用户附加：强制模块、是否 dry-run、是否本轮只做 gap 分析

### Step B — 发现变更 + 全量 Source 清单

**B1 Git 增量（优先）**

- Source：`git log --oneline <lastSourceCommit>..HEAD`
- Source：`git diff --stat <lastSourceCommit>..HEAD -- Services ServerCore Program.cs wwwroot`
- 无增量：仍必须做 **B2 全量**，避免历史未同步功能被当成「已完成」

**B2 全量 Source 业务清单（每轮必做）**

1. 列出 Source **全部** API 路由
2. 列出 Source `GmService*` 等 **对外业务方法**
3. 与 Target 路由/方法做集合差：
   - `SOURCE_ONLY` → 必须 PORT/ADAPT（不能只记一笔）
   - `BOTH_EXIST` → 必须语义 diff（不能默认 KEEP）
   - `TARGET_ONLY` → KEEP，记依据
4. 对 `BOTH_EXIST`：至少对比校验、数量/上限、默认路径、写库字段、成败与主返回字段
5. P0/邮件/背包：对照 Server schema / ItemCore / claim flag

**B3 本轮变更集**

- `SOURCE_CHANGED` / `SOURCE_ONLY` / `TARGET_ONLY` / `BOTH_EXIST` / `PARITY_UNKNOWN`

### Step C — 自完善作业清单

改代码前写入/更新：`docs/sync-state/CURRENT_RUN_PLAN.md`

#### C1 功能映射表（强制）

| ID | 业务能力 | Source 位置 | Target 位置 | 关系 | 1:1? | 本轮动作 | 置信度 | 依据 |
|----|----------|-------------|-------------|------|------|----------|--------|------|
| F01 | … | … | … | DIVERGED | NO | ADAPT | HIGH | … |

- 关系：`SAME | DIVERGED | SOURCE_ONLY | TARGET_ONLY | UNMAPPED | PARITY_UNKNOWN`
- 1:1?：`YES | NO | UNKNOWN`（仅 `YES` 可在无改动时动作 SKIP）
- 动作：`SKIP | KEEP | SYNC | PORT | ADAPT | REMOVE | DEFER`

**映射覆盖要求：**

- C2 必扫链路每一项一行
- Source **每一个** 路由至少映射到一行（可合并到同一业务能力，但不得漏路由）
- `parityGaps` 中历史缺口必须重新出现在表中直到 1:1

#### C2 必扫业务链路

1. 物品发放（堆叠/装备/宠物/装扮/期限）
2. 特殊发放（晶块、复活币、NameTag、Premium、SpecialReward）
3. 邮件发放 vs 直写（direct、在线安全、幂等、审计）
4. 背包/仓库列表、删除、批量删除、受保护槽位
5. 货币/钱包/点券/金币/胜点/金币上限
6. 账号金库 / 个人仓库 / 容量
7. 数量限制、堆叠、满包、重复请求
8. 条件校验顺序、失败语义、返回字段
9. 持久化事务边界与锁
10. 任务/称号簿与发放或位图交叉
11. 账号/角色管理中与 Source 重叠部分
12. 前端发放/背包/任务相关交互
13. 配置、环境、鉴权中影响业务结果的部分
14. 本轮 `SOURCE_CHANGED` 的任何新模块/API
15. **历史 `parityGaps` 中仍未 1:1 的项**

#### C3 自完善规则

- 新 Source 模块 → `PORT`/`ADAPT`，1:1?=NO
- `BOTH_EXIST` 且未证明 1:1 → 禁止 KEEP；`SYNC`/`ADAPT` 或 `DEFER`+写入 parityGaps
- 仅工程优化/装备配置/真正 Target-only → `KEEP`，并写「为何不影响 1:1」
- 旧分叉无独有调用 → ADAPT 后 REMOVE
- 无法对应 → `UNMAPPED`+`DEFER`，禁止瞎改
- knownDivergences 若已 1:1 → 删除该分歧
- 写「本轮 1:1 策略摘要」与「本轮 parityGaps 计划」

#### C4 自动执行门槛

- HIGH/MEDIUM 且 `SYNC|PORT|ADAPT` → **直接实现**
- `PARITY_UNKNOWN` 且属必扫链路 → 本轮至少完成语义 diff；能修则修，不能则 DEFER 进 parityGaps（**不得标成已同步**）
- `UNMAPPED`/LOW 且破坏性 → DEFER，不阻塞其他项
- dry-run → 只 A–C + 完整缺口表，不改业务代码

**默认：非 dry-run 则 C 后自动 D。**  
Hard Stop 仅当：无法判定 Target-only、删除仍有调用、替换整个 Inventory 栈、改变迁移语义、或 1:1 会破坏 Server 协议且无适配方案。

### Step D — 执行同步

#### 优先级

| 优先级 | 范围 |
|--------|------|
| **P0** | 发放、特殊物、删除、货币、满包/堆叠、幂等、事务、错误语义、邮件 |
| **P1** | Source_ONLY 路由/方法；parityGaps 中非 P0 的 BOTH_EXIST 分叉 |
| **P2** | 任务/称号交叉、账号角色重叠语义、读模型字段 |
| **P3** | 前端提示文案级；文档（用户未要求不主动扩写） |

每一轮在时间允许时应 **尽量清空可修的 parityGaps**；不能清空则报告中按 P0→P3 列出剩余缺口，**禁止写「已全部 1:1」**。

#### 实现策略

1. **SYNC** — 重叠分叉改为 Source 行为；工程优化/装备配置须证明不改变结果  
2. **PORT** — Source 有 Target 无：路由→DTO→GmService→ServerCore→前端  
3. **ADAPT** — 结构不同，语义 1:1；禁止复制冲突层  
4. **KEEP** — 仅 Target-only / 已证明不改变结果的工程优化 / 装备配置附加  
5. **REMOVE** — Source 已覆盖且无独有调用的 Target 分叉  
6. **DEFER** — 写入 parityGaps，写清阻塞原因与影响，不假装完成  

#### 编码红线

- 不整目录覆盖 Inventory  
- 不删 SelfTests / 不改断言迁就坏实现  
- 不引入无关依赖  
- API 可兼容扩展字段，不得破坏 Source 已有字段语义  
- 前端遵循 Target `wwwroot/js` 约定  

### Step E — 验证

```bash
dotnet build DfoGmTool.csproj -c Debug
```

相关 SelfTests 必跑。本轮改动涉及的 1:1 场景必测：

1. 普通堆叠发放：落点（邮件/背包）与数量  
2. 装备/装扮/宠物（含 Target options 时：默认路径仍须符合 Source；options 为附加）  
3. 特殊物：晶块/复活币/NameTag/Premium  
4. 默认发放 vs direct  
5. 删物：保护槽失败、正常删除成功  
6. 满包/超量失败语义  
7. 幂等（Source 有则必须）  
8. 货币/金库  
9. 任务交叉（本轮若触达）  
10. Target-only 冒烟未被破坏  
11. 邮件/Server：表列、claim flag、item_core 长度 82  

禁止：把「构建成功」当成 1:1 完成。

### Step F — 报告与基线

#### F1 用户报告（中文，八段）

1. **Source 变更摘要**（commit / 文件）  
2. **本轮达到 1:1 的功能**（映射 ID + 文件）  
3. **以 Source 为准修改的差异**（旧 Target → 新行为）  
4. **保留的 Target-only / 工程优化 / 装备配置**（及为何不影响 1:1）  
5. **验证结果**  
6. **parityGaps / UNMAPPED / DEFER**（剩余未 1:1 清单，按优先级）  
7. **是否可声称「Source 业务已全部 1:1」**：仅当 parityGaps 为空且无 UNMAPPED 阻塞时为 **是**，否则为 **否**  
8. **基线**：`lastSourceCommit`、`lastSyncAt`、`parityGaps` 摘要  

#### F2 更新 `86JPGMTool.sync-state.json`

- 刷新时间与 commit  
- `parityStandard`: `"source-business-1to1"`  
- 更新 `moduleMap`、`knownDivergences`、`parityGaps`、`unmapped`  
- 已 1:1 的从 parityGaps / knownDivergences 移除  

可选归档：`docs/sync-state/runs/YYYYMMDD-HHmm-sync.md`

---

## 2. 判定细则

### 必须 SYNC/ADAPT

同一业务下任一不一致：校验、数量、状态、成败语义、持久化业务含义、默认路径、返回主字段。

### 可以 KEEP

- 真正 Target-only  
- 已证明「相同输入 → 相同业务结果」的工程优化  
- 装备配置附加且不改 Source 默认规则  
- **已语义 diff 证明 1:1** 的 BOTH_EXIST（此时 1:1?=YES，动作 SKIP/KEEP 均可，但须写证据）

### 禁止 KEEP

- 未做语义 diff 的 BOTH_EXIST  
- 「Target 实现更长/更丰富」的重叠业务规则  
- 历史 knownDivergences 未关闭项  

### PORT

Source 有路由/方法/流程而 Target 无，或 Source 修了 bug 而 Target 仍旧。

### DEFER

无法对应、Server/schema 未确认、会替换整 Inventory 栈、删除影响不明。  
DEFER 必须进入 `parityGaps`，下轮 B2 强制再次出现。

---

## 3. 推荐检索顺序

1. Source vs Target `Program.cs` 路由集合差  
2. `Services/GmService*.cs`、`PvfIndexService*.cs` 对外方法  
3. `ServerCore/Game/Inventory|Mailbox|Currency|Premium|Quests|TitleBook|Characters`  
4. 本轮 git diff 符号  
5. `wwwroot/js` give / inventory / quests  
6. Server：`Game/Mailbox`、`ItemCore`、`item_schema.sql`、邮箱迁移  
7. 语义搜索：grant, delete item, wallet, cube, mail, quest complete  

先映射再改代码。

---

## 4. 一键触发短语

- `按 SYNC_FROM_86JPGMTool 提示词执行同步`  
- `同步 86JPGMTool → 当前项目`（默认：**推进 Source 业务 1:1**）  
- `dry-run 同步 86JPGMTool`（只出映射与 parityGaps，不改代码）  
- `全量 1:1 语义 diff`（强制 B2 全路由/方法 diff，可 dry-run 或执行）  
- `清空 parityGaps`（优先修已知缺口）  

收到后执行 Section 1 的 A→F（dry-run 则 A→C + 报告草稿）。

---

## 5. Definition of Done

### 5.1 单轮同步 DoD（本轮可交付）

- [ ] `CURRENT_RUN_PLAN.md` 含映射表，覆盖 C2 + Source 路由 + 历史 parityGaps  
- [ ] 本轮声明处理的 HIGH/MEDIUM 项已落地或显式 DEFER  
- [ ] `dotnet build` 通过；相关测试通过  
- [ ] 八段中文报告齐全  
- [ ] `parityGaps` 已更新（不得把未 1:1 项标成完成）  
- [ ] `86JPGMTool.sync-state.json` 已更新  

### 5.2 全量 1:1 DoD（方可声称「Source 每个业务功能已 1:1」）

- [ ] Source 每一对外路由/业务方法均有映射行且 `1:1?=YES`  
- [ ] `parityGaps` 为空  
- [ ] `unmapped` 无阻塞项（或用户书面接受残留）  
- [ ] 必扫链路均有验证证据（测试或路径审查记录）  
- [ ] 邮件/发放等写库路径已对照 Server  

**未满足 5.2 时，报告第 7 段必须写「否」，并列出剩余缺口。**

---

## 6. 相关文件

| 文件 | 用途 |
|------|------|
| `docs/SYNC_FROM_86JPGMTool.prompt.md` | 本规范（1:1 标准） |
| `docs/sync-state/86JPGMTool.sync-state.json` | 基线、parityGaps、分歧 |
| `docs/sync-state/CURRENT_RUN_PLAN.md` | 本轮清单 |
| `docs/sync-state/runs/` | 历史归档 |
| `/Users/licocon/Downloads/86JP` | 服务端协议对照（只读） |
