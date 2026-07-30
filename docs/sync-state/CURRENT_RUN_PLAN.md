# 本轮同步作业清单（CURRENT_RUN_PLAN）

- 生成时间：2026-07-30（Asia/Shanghai）
- 完成时间：2026-07-30T21:38:46+08:00
- **同步标准**：`source-business-1to1` — Source 每个业务功能须与 Target 1:1；详见 `docs/SYNC_FROM_86JPGMTool.prompt.md` §0.1
- Source：`/Users/licocon/java/86JPGMTool`
- Target：`/Users/licocon/java/86jp_DfoGmTool`
- Server：`/Users/licocon/Downloads/86JP`
- 增量基线：`9fbef108d196164f6189322973dfe6070761bd80..2596f2ecabd48e49179588ca58d9dbb97d56c147`
- Source HEAD（含 merge）：`d587ade1cb8002cd83ace4dff8c5de903e8c70d8`（merge `feat/equipment-mail-options`）
- 功能 commit：`2596f2e feat: 支持自定义装备邮件发放`
- **是否已全部 1:1**：**否**（F05/F06/F10/F11/F17 仍有残留；本轮清空 F-grant 装备邮件配置 + F-mail 显式附件 + F12 装备发放 UI）

## 本轮变更集

- `SOURCE_CHANGED`：`2596f2e`（+ merge d587ade）
  - Program.cs：`GiveItem` 增加 `equipmentOptions`
  - `Services/EquipmentGrantOptions.cs`（Source 新建）
  - `ItemAmplifier` + `ItemUpgradeTableProvider.ResetForPvfChange`
  - `MailboxRepository` 显式 `ItemCoreData` 附件 + hash
  - `GmService.Inventory` 装备邮件配置/多附件
  - 前端 give 装备弹窗 + CSS
- Target ADAPT（非整包覆盖）：
  - 复用已有 `ItemGrantOptions` + `EquipmentGrantPolicy` / `AmplifyInitialValueResolver`（按属性类型）
  - 普通装备配置走邮件；装扮/期限/手动分类保持 Target 直写
  - 前端在现有 give-config 卡片上补 Source 的 state 语义，不另开独立 modal

## 功能映射表（本轮重点）

| ID | 业务能力 | Source | Target | 关系 | 1:1? | 本轮动作 | 置信度 |
|----|----------|--------|--------|------|------|----------|--------|
| F01 | 普通装备自定义邮件发放 | GiveItem+equipmentOptions+多附件 | GiveItem+options.state+多附件 ItemCore | ADAPTED | YES | ADAPT | HIGH |
| F03 | 邮件 vs 直写 | 自定义装备禁止 direct | 同 | SAME | YES | ADAPT | HIGH |
| F-mail | 显式 ItemCore 附件 | TryCreateExplicitSystemAttachmentSnapshot | 同语义 | ADAPTED | YES | ADAPT | HIGH |
| F12 | 前端装备发放 | 独立 modal | give-config 卡片 state 分段 | ADAPTED | YES* | ADAPT | HIGH |
| F05–F11/F17 | 其余缺口 | — | — | — | NO | DEFER | — |

\* 字段契约：`state/upgradeLevel/amplifyType/forgingLevel/qualityMode`；API 形状 Target 用 `options` 而非 Source `equipmentOptions`（业务语义 1:1）。

## 执行记录

- [x] Step A 加载状态（基线 9fbef10）
- [x] Step B 增量 2596f2e + 功能 diff
- [x] Mailbox 显式 ItemCoreData + hash
- [x] GiveItem 装备邮件配置 / 多附件 ≤10 / direct 禁止自定义
- [x] AmplifyInitialValueResolver 按属性类型
- [x] CanHaveAmplifyState 与 CanAmplify 拆分
- [x] 前端 give.js / style / README
- [x] build + item-grant-options + inventory-migration + character-mutations
- [x] 更新 sync-state

## 验证结果

| 检查 | 结果 |
|------|------|
| `dotnet build -c Debug` | OK 0W0E |
| `--selftest-item-grant-options` | OK |
| `--selftest-inventory-migration` | OK |
| `--selftest-character-mutations` | OK（`PVF_ARCHIVE_PATH=/Users/licocon/java/pvf/Script.pvf`） |

## parityGaps（更新后）

| ID | 状态 | 说明 |
|----|------|------|
| F05 | PARTIAL | GoldLimit clamp |
| F06 | PARTIAL | max API Target-only |
| F10 | PARITY_UNKNOWN | Search limit / title 绑定 |
| F11 | PARTIAL | growtype Fixed 超集 |
| F17 | DEFER_ADAPT | Inventory 整栈禁止覆盖 |

下一轮建议：F10 Search 默认 limit；F05 GoldLimit 是否 KEEP 增强。
