# 文档与同步

## Source → Target 持续同步

| 文件 | 说明 |
|------|------|
| [SYNC_FROM_86JPGMTool.prompt.md](./SYNC_FROM_86JPGMTool.prompt.md) | 长期同步作业提示词（交给 AI 执行） |
| [sync-state/86JPGMTool.sync-state.json](./sync-state/86JPGMTool.sync-state.json) | 同步基线与已知分歧 |
| [sync-state/CURRENT_RUN_PLAN.md](./sync-state/CURRENT_RUN_PLAN.md) | 本轮作业清单（每轮由 AI 覆盖） |
| `sync-state/runs/` | 历史轮次归档 |

### 每次 Source 更新后

对 AI 说：

```text
按 docs/SYNC_FROM_86JPGMTool.prompt.md 执行本轮同步
```

只分析不改代码：

```text
dry-run 同步 86JPGMTool
```

Source 固定为：`/Users/licocon/java/86JPGMTool`  
Target 固定为：本仓库 `/Users/licocon/java/86jp_DfoGmTool`
