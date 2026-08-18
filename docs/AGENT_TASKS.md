# 给 AI 的作业入口

先选作业类型，再复制对应短指令。规范正文按类型分文件，不要把同步、移植、修 bug 写进同一段。

写库与默认路径：[`INVARIANTS.md`](./INVARIANTS.md)。仓库惯例：根目录 `AGENTS.md`。

收到长任务先判定类型，再读对应规范。用户写了 `CURRENT_RUN_PLAN` 四个字、但 Source 不是 `86JPGMTool` 时，按移植或本仓库任务处理，清单写 `CURRENT_TASK.md`。

| 作业 | 对 AI 说 | 规范 |
|------|----------|------|
| 从 86JPGMTool 同步 | `按 docs/SYNC_FROM_86JPGMTool.prompt.md 执行本轮同步` | [`SYNC_FROM_86JPGMTool.prompt.md`](./SYNC_FROM_86JPGMTool.prompt.md) |
| 只分析同步缺口 | `dry-run 同步 86JPGMTool` | 同上，只做 A–C |
| 从其他 DfoGmTool 树移植 | 用下面「移植」模板 | 本文件；**不是** 86JPGMTool 同步 |
| 本仓库新功能 / 修 bug | 用下面「本轮任务」模板 | [`INVARIANTS.md`](./INVARIANTS.md) |
| 只审查 | 用下面「审查」模板 | 不改代码 |

清单文件：

- `docs/sync-state/CURRENT_RUN_PLAN.md`：**仅** 86JPGMTool 同步的本轮映射。其他任务不要覆盖。
- `docs/sync-state/CURRENT_TASK.md`：移植或本仓库任务的本轮映射。

---

## 本轮任务（本仓库功能 / 修 bug）

```text
按 docs/INVARIANTS.md 实现。只改 Target=/Users/licocon/java/86jp_DfoGmTool。
Source=无。Server=/Users/licocon/java/ServerS4A12/Server/DfoServer（只读）。
不是 docs/SYNC_FROM_86JPGMTool.prompt.md 作业。不要同步 86JPGMTool。
映射写入 docs/sync-state/CURRENT_TASK.md，不要覆盖 CURRENT_RUN_PLAN.md。

目标：<一句话>

完成标准：
- API：<方法 路径 关键字段>
- 默认路径：未指定时 <邮件 / 背包 / 其他>
- 玩家路径保持：ClaimMail / DeleteMail 不变
- UI：<页签 / 确认文案>
- 对照 INVARIANTS：合法 ID 空集拒绝、共享邮件只动当前 recipient、切角色校验 epoch+characterId

验证：dotnet build；按改动跑对应 --selftest-* 。报告用中文。
```

---

## 移植（Source 不是 86JPGMTool）

```text
按 docs/INVARIANTS.md 移植。Source 只读，Target 落代码，Server 写库权威。

Source=<磁盘树>
Target=/Users/licocon/java/86jp_DfoGmTool
Server=/Users/licocon/java/ServerS4A12/Server/DfoServer

不是 docs/SYNC_FROM_86JPGMTool.prompt.md。不要同步 /Users/licocon/java/86JPGMTool。
不要整包覆盖 NewInventoryStore / MailboxRepository 玩家路径。
映射写入 docs/sync-state/CURRENT_TASK.md。

先列 Source 与 Target 的路由差、GmService 对外方法差、wwwroot 交互差，再 PORT/ADAPT。
SOURCE_ONLY 的 GM 业务默认做掉；BOTH_EXIST 先语义 diff；PvfLib/磁盘索引 KEEP Target，除非改了业务结果。
未指定发放保持 Target 默认系统邮件。

完成标准：每个对外入口一行映射（PORT/ADAPT/KEEP/SKIP/DEFER）。DEFER 写原因。
验证与 INVARIANTS 自测。报告用中文。
```

---

## 审查

```text
只 review，不改代码。范围：<未提交 vs HEAD / 指定文件>。
对照 docs/INVARIANTS.md。
Critical：数据丢失、写错库、默认发放被改、空合法 ID 清理。
Important：共享邮件、过期 recipient 挡住 GM 删除、切角色竞态、孤立审计。
Minor：文案、计数、busy 状态。
不要报纯风格。
```

---

## 86JPGMTool 同步（短指令）

```text
按 docs/SYNC_FROM_86JPGMTool.prompt.md 执行本轮同步。
Source=/Users/licocon/java/86JPGMTool
Target=/Users/licocon/java/86jp_DfoGmTool
Server=<用户给出的 DfoServer 根，磁盘存在为准>
```

附加一句即可：`dry-run`、`全量 1:1 语义 diff`、`清空 parityGaps`、`本轮额外关注：<ID>`。
