# 给 AI 的作业入口

先选作业类型，再复制对应短指令。规范正文按类型分文件，不要把同步、移植、修 bug 写进同一段。

写库与默认路径：[`INVARIANTS.md`](./INVARIANTS.md)。UI / 交互 / 性能：[`UX.md`](./UX.md)。仓库惯例：根目录 `AGENTS.md`。

收到长任务先判定类型，再读对应规范。用户写了 `CURRENT_RUN_PLAN` 四个字、但 Source 不是 `86JPGMTool` 时，按移植或本仓库任务处理，清单写 `CURRENT_TASK.md`。

| 作业 | 对 AI 说 | 规范 |
|------|----------|------|
| 从 86JPGMTool 同步 | `按 docs/SYNC_FROM_86JPGMTool.prompt.md 执行本轮同步` | [`SYNC_FROM_86JPGMTool.prompt.md`](./SYNC_FROM_86JPGMTool.prompt.md) |
| 只分析同步缺口 | `dry-run 同步 86JPGMTool` | 同上，只做 A–C |
| 从其他 DfoGmTool 树移植 | 用下面「移植」模板 | 本文件；**不是** 86JPGMTool 同步 |
| 本仓库新功能 / 修 bug | 用下面「本轮任务」模板 | [`INVARIANTS.md`](./INVARIANTS.md) |
| 全仓库审查 | `全仓库 review` | 本文件 + INVARIANTS + UX；不改代码 |
| UI / 交互 / 性能审查 | `UI review` | [`UX.md`](./UX.md)；不改代码 |
| 增量审查 | 用下面「增量审查」模板 | 不改代码 |

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
- UI：<页签 / 确认文案>；对照 docs/UX.md（代次、busy、分页、escapeHtml）
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
wwwroot 对照 docs/UX.md：代次、busy、分页、escapeHtml；不要另起 UI 框架。
SOURCE_ONLY 的 GM 业务默认做掉；BOTH_EXIST 先语义 diff；PvfLib/磁盘索引 KEEP Target，除非改了业务结果。
未指定发放保持 Target 默认系统邮件。

完成标准：每个对外入口一行映射（PORT/ADAPT/KEEP/SKIP/DEFER）。DEFER 写原因。
验证与 INVARIANTS 自测。报告用中文。
```

---

## 全仓库审查

```text
全仓库 review。只 review，不改代码。
范围：整个 Target=/Users/licocon/java/86jp_DfoGmTool（含未提交，不只 diff）。
不是 docs/SYNC_FROM_86JPGMTool.prompt.md。不要同步 86JPGMTool。
对照 docs/INVARIANTS.md、docs/UX.md、AGENTS.md。
Server=/Users/licocon/java/ServerS4A12/Server/DfoServer（只读；磁盘不存在就写环境缺口）。

按模块走完，缺一项在报告里标未查：
1. Program.cs 每个写库路由是否落到 GmService，有无裸 SQL
2. GiveItem 默认路径 / deliveryMode / options!=null 是否被当成背包开关
3. 邮件玩家 ClaimMail、DeleteMail；GM 列表/删信/删附件/清空；remainingAfter；审计无 FK
4. 异常清理：PVF 未就绪或合法 ID 空集必须拒绝
5. 账号备份 mailbox_*、孤立审计、owner CASCADE
6. NewInventoryStore / ItemCore.Size==82 / 领取 flag 对照 Server
7. 前端结构：bindings.js 最后加载；页签与顶栏入口能对上；innerHTML 是否 escapeHtml
8. 交互：切角色校验 epoch+characterId；写库按钮 busy/finally；破坏性确认文案与真实 API 范围一致
9. 性能：物品浏览 limit/offset；背包/邮箱不按行打详情；异常清理 running 态；PVF 不进全量内存
10. 自测缺口：写库路径有无对应 --selftest-*

严重级：
Critical：数据丢失、写错库、默认发放被改、空合法 ID 会清空背包、切角色后按钮打到新角色、飞行中双发写库、一次查询/渲染拖死进程
Important：共享邮件、过期 recipient 挡住 GM 删除、孤立审计、玩家路径被改、busy 漏解、确认文案范围与 API 不符、分页无效
Minor：文案、计数、间距、toast 时长

每个问题写：文件、当前行为、对照哪条 INVARIANTS/UX 或 Server 文件、风险、建议。
clone/backup/migration/configure/磁盘 PVF 索引是 Target-only，不要当缺陷，除非写库违反 Server 或把浏览器/进程打满。
不要报纯风格。报告用中文。
```

---

## UI / 交互 / 性能审查

```text
UI review。只 review，不改代码。
范围：整个 Target wwwroot + 相关列表/分页/扫描 API（含未提交）。
不是 docs/SYNC_FROM_86JPGMTool.prompt.md。不要同步 86JPGMTool。
对照 docs/UX.md。写库对错仍对照 docs/INVARIANTS.md，但本作业重点是点错、串角色、卡死。

按 docs/UX.md「审查怎么走」1–7 走完，缺一项标未查。页签至少覆盖：
发放、背包、邮箱、任务、属性/复制/删角色、账号备份恢复、迁移、异常清理。

每个问题写：文件、当前行为、对照 UX 哪条、风险、建议。
不要报纯风格、不要建议上 React、不要为好看改主题变量名。
报告用中文，按 Critical / Important / Minor。
```

---

## 增量审查

```text
只 review，不改代码。范围：未提交 vs HEAD。
对照 docs/INVARIANTS.md；改了 wwwroot 或列表 API 再对照 docs/UX.md。
Critical：数据丢失、写错库、默认发放被改、空合法 ID 清理、切角色串数据、飞行中双发、确认范围与 API 不符。
Important：共享邮件、过期 recipient 挡住 GM 删除、孤立审计、busy 漏解、分页无效、未 escapeHtml。
Minor：文案、计数、间距、toast。
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
