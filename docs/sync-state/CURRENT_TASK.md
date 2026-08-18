# 本轮非同步任务清单（CURRENT_TASK）

- 用途：移植 / 本仓库功能 / 修 bug。**不要**覆盖 `CURRENT_RUN_PLAN.md`（那份只给 86JPGMTool 同步）。
- 最近一次：2026-08-16 从磁盘树 `/Users/licocon/Downloads/86jp_DfoGmTool` 移植 + 角色邮件管理。

## 功能映射表

| ID | 能力 | Source 位置 | Target 位置 | 关系 | 本轮动作 | 依据 |
|----|------|-------------|-------------|------|----------|------|
| A01 | POST `/api/characters/{id}/mailbox/clear` | `Program.cs`、`GmService.Mailbox.cs` | `Program.cs` + `GmService.Mailbox.cs` + `MailboxRepository.GmAdmin.cs` | SOURCE_ONLY | ADAPT | SQL 进 MailboxRepository |
| A02 | GET/POST `/api/inventory-anomalies/*` | `Program.cs`、`GmService.InventoryAnomalies.cs` | 同上 | SOURCE_ONLY | PORT | 空合法 ID 拒绝清理 |
| A03 | 账号页「异常物品清理」UI | `wwwroot` | `inventory-anomalies.js` | SOURCE_ONLY | PORT | |
| A04 | 发放方式切换 | `give.js` | `deliveryMode`；未指定默认邮件 | BOTH_EXIST | ADAPT | `direct` 自测兼容 |
| A05 | 备份含邮箱 | Source v2 | Target v1 + mailbox_* | BOTH_EXIST | ADAPT | 恢复丢掉孤立审计 |
| A06 | 多封/上限/幂等 | `GmSystemMailService` | `GiveItemViaMail` + `SendSystemMails` | SOURCE_ONLY | ADAPT | |
| A07 | 邮件附件草稿 | Source Mail.cs | Target `TryCreateMailAttachments` | SOURCE_ONLY | KEEP | |
| A08 | InventoryAnomaly partial | Source | `NewInventoryStore.InventoryAnomaly.cs` | SOURCE_ONLY | PORT | |
| A09 | DatabaseCompatibilityGuard v52 | Source | Target 迁移器 | SOURCE_ONLY | DEFER | |
| A10 | CopyValidItemIds | Source HashSet | 磁盘索引 | SOURCE_ONLY | ADAPT | |
| A11 | quests activationId | Source | Target 无列 | SOURCE_ONLY | DEFER | |
| A12 | PvfLib / mmap | Source 内存 | Target 磁盘索引 | BOTH_EXIST | KEEP | |
| A13 | 默认系统邮件 | Source | `GiveItemViaMail` | BOTH_EXIST | KEEP | |
| A14 | 玩家 DeleteMail / ClaimMail | Server | 同语义 | BOTH_EXIST | KEEP | |
| B01–B03 | 角色邮箱管理 | 本次新增 | GM 列表/删邮件/删附件/页签 | TARGET_ONLY | PORT | remainingAfter 只计活动收件箱 |
