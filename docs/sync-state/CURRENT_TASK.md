# 本轮非同步任务清单（CURRENT_TASK）

- 用途：移植 / 本仓库功能 / 修 bug。**不要**覆盖 `CURRENT_RUN_PLAN.md`（那份只给 86JPGMTool 同步）。
- 最近一次：2026-08-18 全仓库审查问题修复（切角色串写、邮件 remainingAfter 过期、名称装饰卡发放路由、busy/分页/自测）。

## 功能映射表

| ID | 能力 | Source 位置 | Target 位置 | 关系 | 本轮动作 | 依据 |
|----|------|-------------|-------------|------|----------|------|
| F01 | 切角色清背包配置卡并校验 characterId | 审查 Critical-1 | `sidebar.js` `inventory.js` | TARGET_ONLY | PORT | 打开卡时记下 characterId+epoch；提交拒绝切走后的旧卡 |
| F02 | 任务库旧行切角色后不可点 | 审查 Critical-2 | `core.js` `quests.js` | TARGET_ONLY | PORT | tbody 纳入切角色清空；搜索校验 epoch+characterId；行按钮绑定当时角色 |
| F03 | GM `remainingAfter` 过滤过期收件人 | 审查 Important-3 | `MailboxRepository.GmAdmin.cs` | TARGET_ONLY | PORT | JOIN messages；未无限且已过期的 peer 不挡住拆根。InboxRecipientCount / CanDelete 不过滤过期 |
| F04 | 玩家 ClaimMail / DeleteMail 对齐 Server | Server `MailboxRepository.cs` | Target 同文件玩家路径 | BOTH_EXIST | ADAPT | folder=0 + saved/无限/未过期；Delete 写 read_flag/read_at；GM API 不走玩家删除 |
| F05 | 名称装饰卡拒绝邮件发放 | 审查 Important-5 | `GmService.Inventory.cs` | TARGET_ONLY | PORT | 非 inventory/direct 返回错误；自测改 `direct:true` 并断言邮件失败 |
| F06 | 发放浏览丢弃过期响应 | 审查 Important-6 | `give.js` `searchItems` | TARGET_ONLY | PORT | requestId；签名变化仍清配置卡 |
| F07 | 切角色背包页码归零 | 审查 Important-7 | `selectCharacter` | TARGET_ONLY | PORT | `invPage=0` 并清配置卡 |
| F08 | 写库按钮 busy | 审查 Important-8 | inventory/sidebar/quests/character | TARGET_ONLY | PORT | acquireWriteLock；URL 用点击时 characterId |
| F09 | 异常清理 busy 必解 | 审查 Important-9 | `inventory-anomalies.js` | TARGET_ONLY | PORT | finally 无条件清 busy |
| F10 | 任务库 / 异常列表明细分页 | 审查 Important-10/11 | `quests.js` `inventory-anomalies.js` `index.html` | TARGET_ONLY | PORT | 摘要全量计数，表格只画当前页 |
| F11 | 自测缺口 | 审查 Important-12 | MailboxGm / CharacterMutation / InventoryAnomaly | TARGET_ONLY | PORT | expire_at 拆根；deliveryMode/options；空合法 ID 拒绝 |
| F12 | 死代码 / bindings / SQL 进仓储 / escapeHtml / 邮箱行按钮 | 审查 Minor | quests/bindings/migration/MailboxRepository/character/mailbox | TARGET_ONLY | PORT | `GiveItemViaMail` 身份查询进 `TryLoadActiveCharacterMailIdentity` |
