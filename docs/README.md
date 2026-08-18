# 文档与同步

给 AI 的作业先从这里选类型，不要把同步、移植、修 bug 写进同一段。

| 文件 | 说明 |
|------|------|
| [AGENT_TASKS.md](./AGENT_TASKS.md) | **作业入口**：短指令与模板 |
| [INVARIANTS.md](./INVARIANTS.md) | 发放 / 背包 / 邮件 / 异常清理 / 备份写库规则 |
| [UX.md](./UX.md) | UI / 交互 / 列表性能 |
| [SYNC_FROM_86JPGMTool.prompt.md](./SYNC_FROM_86JPGMTool.prompt.md) | 仅 86JPGMTool → Target 的 1:1 同步规范 |
| [sync-state/86JPGMTool.sync-state.json](./sync-state/86JPGMTool.sync-state.json) | 同步基线、parityGaps |
| [sync-state/CURRENT_RUN_PLAN.md](./sync-state/CURRENT_RUN_PLAN.md) | **仅** 86JPGMTool 同步的本轮映射 |
| [sync-state/CURRENT_TASK.md](./sync-state/CURRENT_TASK.md) | 移植 / 本仓库任务的本轮映射 |
| `sync-state/runs/` | 历史轮次归档 |

仓库惯例：根目录 [`AGENTS.md`](../AGENTS.md)。主 README 同步段落：[`README.md`](../README.md) → **从 86JPGMTool 同步业务**。

## 86JPGMTool 同步（复制短指令）

```text
按 docs/SYNC_FROM_86JPGMTool.prompt.md 执行本轮同步。
Source=/Users/licocon/java/86JPGMTool
Target=/Users/licocon/java/86jp_DfoGmTool
Server=<用户给出的 DfoServer 根，磁盘存在为准>
```

附加一句即可：`dry-run`、`全量 1:1 语义 diff`、`清空 parityGaps`、`本轮额外关注：<ID>`。

| 角色 | 路径 |
|------|------|
| **Source（业务权威）** | `/Users/licocon/java/86JPGMTool` |
| **Target（本仓库）** | `/Users/licocon/java/86jp_DfoGmTool` |
| **Server（协议）** | 用户消息优先；常见 `.../ServerS4A12/Server/DfoServer` 或 `.../Downloads/86JP/Server/DfoServer` |

移植、本仓库功能、全仓库审查、UI 审查：用 [`AGENT_TASKS.md`](./AGENT_TASKS.md) 里对应模板。写库对照 [`INVARIANTS.md`](./INVARIANTS.md)，界面对照 [`UX.md`](./UX.md)。

短指令：`全仓库 review`；只要界面与卡顿：`UI review`。
