# DfoGmTool 写库与默认路径（Agent 对照）

改发放、背包、邮件、异常清理、账号备份时必读。用磁盘上的 Server 文件验证，不要只信 git log。

**听谁的：** Server 管协议（表、ItemCore、领取 flag）；Target 管工程实现（`NewInventoryStore`、磁盘 PVF 索引、备份 v1）；86JPGMTool 同步时重叠**业务规则**才以 Source 为准。三者打架时：协议跟 Server，结构跟 Target，不要为对齐 Source 去覆盖 Inventory / 玩家邮件路径。

Server 根：用户消息优先。常见 `/Users/licocon/java/ServerS4A12/Server/DfoServer` 或 `/Users/licocon/Downloads/86JP/Server/DfoServer`。

## 落点

- 路由只加 `Program.cs`
- 对外业务放 `Services/GmService.*.cs`
- 邮件/背包 SQL 放 `ServerCore` 对应 Repository；GM 特权另开方法
- 新前端绑定只进 `wwwroot/js/bindings.js`（该文件必须最后加载）
- 在 Target 模型上 ADAPT。整包覆盖 `NewInventoryStore` 或 `MailboxRepository` 玩家路径即失败

## 发放默认路径

- 未指定或 `deliveryMode=mail`：系统邮件
- `deliveryMode=inventory` 或 `direct=true`：直写背包
- 装扮属性 / 期限天数 / 手动分类：仍直写背包（邮件附件表达不了）
- 装备自定义属性（净化/强化/增幅/锻造）：只走邮件 ItemCore，禁止直写
- 有 `options` 不等于走背包

## 邮件

- `ItemCore.Size == 82`；`mailbox_attachments.item_core` 为 82 字节 BLOB
- 领取：附件用 `0x40000000 | attachment_id`；纯金币/文本用 `messageId`
- `claimed_flag`：0 未领 / 1 已领 / 2 领取中
- 玩家 `DeleteMail`：有未领金币或 `claimed_flag=0` 附件 → 失败
- GM 删除：丢掉未领附件，物品不进背包；不走玩家 DeleteMail
- 「还有人持有」= 其他角色、`folder=0`、`deleted_flag=0`。过期/软删/发件箱不算
- `mailbox_system_mail_audit` 对 messages 无 FK：删根消息必须显式清审计；恢复备份丢掉没有对应 message 的审计行
- `mailbox_campaign_deliveries.message_id` 是 ON DELETE SET NULL，不要删 campaign 行
- 过期索引：`(unlimited_flag, expire_at, message_id)`
- 恢复备份：当前账号若是邮件 owner，CASCADE 会去掉其他人共享的 recipient；保持备份 v1，不要为对齐 Source v2 改版本号

## 异常清理

- PVF 未就绪，或合法物品 ID 集合为空：拒绝扫描和清理
- 空集合当「全部非法」会清空全库背包

## 前端代次

- 异步写 DOM 前同时校验 `selectEpoch` 和当前 `characterId`
- 破坏性确认写明：未领取附件会消失，不会进入背包
- UI / 交互 / 性能细则：[`UX.md`](./UX.md)

## 自测

```bash
dotnet build DfoGmTool.csproj -c Debug
dotnet run -- --selftest-item-grant-options
dotnet run -- --selftest-inventory-migration
dotnet run -- --selftest-mailbox-gm
dotnet run -- --selftest-character-mutations
```

`--selftest-character-mutations` 需要本机 `Script.pvf` 或 `PVF_ARCHIVE_PATH`。没有就在报告里写环境缺口，不要标通过。
