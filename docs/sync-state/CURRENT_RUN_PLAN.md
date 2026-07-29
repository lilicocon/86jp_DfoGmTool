# 本轮同步作业清单（CURRENT_RUN_PLAN）

> 状态：**尚未执行正式同步轮次**  
> 本文件由 Agent 在每次同步的 **Step C** 覆盖更新。  
> 用户触发同步后，请以 `docs/SYNC_FROM_86JPGMTool.prompt.md` 为准自动完善下表。

## 触发方式

```text
按 docs/SYNC_FROM_86JPGMTool.prompt.md 执行本轮同步
```

或 dry-run：

```text
dry-run 同步 86JPGMTool
```

## 基线（来自 sync-state）

| 项 | 值 |
|----|-----|
| Source | `/Users/licocon/java/86JPGMTool` |
| Target | `/Users/licocon/java/86jp_DfoGmTool` |
| lastSourceCommit | 见 `86JPGMTool.sync-state.json` |
| lastSyncAt | null（未完成正式同步） |

## 功能映射表（占位 — 下轮 Step C 必须重写）

| ID | 业务能力 | Source 位置 | Target 位置 | 关系 | 本轮动作 | 置信度 | 依据 |
|----|----------|-------------|-------------|------|----------|--------|------|
| F01 | 物品发放 | `GmService.GiveItem` + Mail/Direct | `GiveItem` + `NewInventoryStore.TryGrant` | DIVERGED | DEFER | MEDIUM | 初始化占位，待本轮 diff 核实 |
| F02 | 邮件发放 | `ServerCore/Game/Mailbox/**` | （缺失） | SOURCE_ONLY | DEFER | MEDIUM | 需 ADAPT 到新版背包，禁止整包覆盖 |
| F03 | 背包列表/删除 | `GmService.Inventory` | `GmService.Inventory` | BOTH_EXIST | SKIP | LOW | 待语义 diff |
| F04 | 货币/晶块 | Accounts + CurrencyService | 同左 + GoldLimits | BOTH_EXIST | SKIP | LOW | 待语义 diff |
| F05 | 角色复制/备份 | — | CharacterClone / AccountBackup | TARGET_ONLY | KEEP | HIGH | Target 独有 |

## 本轮变更集

- SOURCE_CHANGED: （待 Step B 填充）
- SOURCE_ONLY: （待填充）
- TARGET_ONLY: （待填充）

## 同步策略摘要

（待 Agent 在 Step C 根据代码扫描自动填写）

## 执行记录

- 尚未执行。
