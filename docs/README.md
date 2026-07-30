# 文档与同步

## Source → Target 持续同步

| 文件 | 说明 |
|------|------|
| [SYNC_FROM_86JPGMTool.prompt.md](./SYNC_FROM_86JPGMTool.prompt.md) | 长期同步作业提示词（**Source 业务 1:1**） |
| [sync-state/86JPGMTool.sync-state.json](./sync-state/86JPGMTool.sync-state.json) | 同步基线、parityGaps、已知分歧 |
| [sync-state/CURRENT_RUN_PLAN.md](./sync-state/CURRENT_RUN_PLAN.md) | 本轮作业清单（每轮由 AI 覆盖） |
| `sync-state/runs/` | 历史轮次归档 |

| 角色 | 路径 |
|------|------|
| **Source（业务权威）** | `/Users/licocon/java/86JPGMTool` |
| **Target（本仓库）** | `/Users/licocon/java/86jp_DfoGmTool` |
| **Server（协议/表结构）** | `/Users/licocon/Downloads/86JP`（DfoServer） |

主 README 同款段落：仓库根目录 [`README.md`](../README.md) → **从 86JPGMTool 同步业务**。

### 同步标准（必读）

- **Source 有的每个业务功能，Target 必须 1:1 一致**（默认路径、校验、数量、状态、事务、成败与主返回字段）。
- 允许在新版 ItemCore 上 **ADAPT**，禁止因模型不同改变业务结果；禁止整包覆盖 Inventory。
- Target-only（clone/backup/migration/configure 等）与装备配置附加能力可保留。
- 未证明 1:1 的重叠功能 **不能 KEEP 糊弄**；记入 `parityGaps` 直至对齐。
- 写库路径须可被 Server 消费（Mailbox / ItemCore）。

---

## 每次 Source 更新后：复制下面整段发给 AI

### 正式同步（推进 1:1，推荐）

```text
按 docs/SYNC_FROM_86JPGMTool.prompt.md 执行本轮同步。

Source=/Users/licocon/java/86JPGMTool
Target=/Users/licocon/java/86jp_DfoGmTool
Server=/Users/licocon/Downloads/86JP

标准：Source 每个业务功能与 Target 1:1 一致（不只 P0）。

要求：
1. 先读提示词与 docs/sync-state/86JPGMTool.sync-state.json
2. Step B：git 增量 + 全量 Source 路由/对外方法清单，维护 parityGaps
3. Step C：写入 CURRENT_RUN_PLAN（映射表含 1:1? 列；BOTH_EXIST 禁止未 diff 就 KEEP）
4. 自动执行可确认的 SYNC/PORT/ADAPT；P0 优先，并尽量消化历史 parityGaps
5. 禁止整包覆盖 Target Inventory；Target-only 与装备配置 KEEP
6. 邮件/背包写库对照 Server（表结构、ItemCore、领取 flag）
7. 构建与相关 SelfTests；报告必须回答「是否已全部 1:1」；未完成则列出 parityGaps
8. 更新 sync-state 基线与 parityGaps
```

### 全量 1:1 语义 diff（可先 dry-run）

```text
全量 1:1 语义 diff。
按 docs/SYNC_FROM_86JPGMTool.prompt.md：
对 Source 每一个 API 路由与对外业务方法做映射与语义对比，
写出 CURRENT_RUN_PLAN 与完整 parityGaps。先 dry-run 不改代码。
```

### 只分析、不改代码（dry-run）

```text
dry-run 同步 86JPGMTool。
按 docs/SYNC_FROM_86JPGMTool.prompt.md 只执行 Step A–C：
对比 Source=/Users/licocon/java/86JPGMTool 与当前项目，
写出映射表与 parityGaps，不要改业务代码。
```

### 可选附加

```text
本轮额外关注：<例如 F04 删物 / F05 货币 / 任务交叉>
上次同步基线：<Source commit，无则省略>
清空 parityGaps
```

### 一句话速记

| 场景 | 对 AI 说 |
|------|----------|
| 推进 1:1 同步 | `按 docs/SYNC_FROM_86JPGMTool.prompt.md 执行本轮同步` |
| 全量缺口分析 | `全量 1:1 语义 diff`（可加 dry-run） |
| 只看差异 | `dry-run 同步 86JPGMTool` |
