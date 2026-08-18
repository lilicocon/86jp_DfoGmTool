# DfoGmTool UI / 交互 / 性能（Agent 对照）

审查或改 `wwwroot/`、列表 API、PVF 索引时必读。写库规则仍以 [`INVARIANTS.md`](./INVARIANTS.md) 为准。

不要用「体验不好」「再优化一下」当结论。每个问题写：文件、当前行为、对照哪条、会卡死/点错/串角色的风险、建议。

**不是缺陷：** 原生 HTML/JS（没有 React）、四套主题色差、任务搜索默认 limit 500（相对 Source 30 的超集）、磁盘 PVF 索引本身。

---

## UI

- 栈：`wwwroot/index.html` + `js/*.js` + `style.css`。不引入新 UI 框架、构建器和组件库
- 脚本顺序以 `index.html` 底部注释为准；**新事件绑定只进 `bindings.js`，且该文件必须最后加载**
- 角色详情页签与发放 / 背包 / 属性 / 任务同级；账号级危险操作放顶栏 `mini danger`（迁移、异常清理）
- 颜色走 CSS 变量（`--toast-*` 等），硬编码色会在白/天蓝/纯黑/经典主题下炸
- 用户名、邮件标题、物品名、任务名进 `innerHTML` 前走 `escapeHtml`
- 表空态 / 加载中 / 失败都要有 hint，不要空白表或留下一角色数据
- 发放默认展示系统邮件；切到背包必须现有确认文案（直接写库）
- 破坏性按钮用现有 `confirm` / `prompt` / 迁移对话框，不要另做一套 modal 栈

页签与入口必须还能找到：

| 入口 | 文件 | 看什么 |
|------|------|--------|
| 顶栏数据源 / 登录 / 主题 | `environment.js` `theme.js` | 未就绪时发放分类空、状态文案 |
| 账号侧栏 / 备份恢复 | `sidebar.js` | 恢复要二次输入「恢复账号」 |
| 发放 | `give.js` | 分页浏览、配置卡、发放方式 |
| 背包 | `inventory.js` | 分类计数、分页、配置、批量删 |
| 邮箱 | `mailbox.js` | 列表、展开附件、删信/附件/清空 |
| 属性 / 复制 / 转职 | `character.js` | 删角色二次确认 |
| 任务 | `quests.js` | 批量完成禁用、搜索 |
| 迁移 | `migration.js` | 专用确认框，失败已回滚文案 |
| 异常清理 | `inventory-anomalies.js` | 全账号范围、不可撤销文案 |

---

## 交互

### 代次（串角色）

- `selectCharacter` 一开始就 `selectEpoch++` 并清掉旧 `currentChar`，再发请求
- 异步写 DOM 或把按钮绑到角色上之前：**同时**校验 `selectEpoch` 和 `characterId`（只校验 epoch 不够：同代次理论不会，但回调用了闭包里的 `currentChar` 仍会打到新角色）
- 账号工作区重置（`resetAccountWorkspace`）也要加代次，邮箱快照清掉
- 数据源切换用 `runtimeSourceEpoch`，不要把旧 `/api/accounts` 写进新库界面

### 飞行中（连点）

进行中必须禁用触发按钮，在 `finally` 解开。已有旗标：`giveRequestInFlight`、`mailboxBusy`、`inventoryAnomalyBusy`、`inventoryMigrationBusy`、任务批量按钮。

- 发放：配置卡提交、浏览页发放、发放方式切换，飞行中全禁用
- 邮箱：busy 盖住删/清空；**列表刷新不要一直占着 busy**（刷新完应能再点）
- 异常清理 / 迁移：`running` 时禁用再点，状态文案要说正在扫/正在迁
- 不要用「成功 toast 了但按钮仍可再发」混过

### 确认与反馈

- 会丢物品的操作：确认文案写明不可撤销，以及「未领附件不会进背包」
- 二次输入：删角色「删除角色」、恢复备份「恢复账号」
- 成功/失败都走 `toast`；失败不要只 `console`
- 在线角色改库：顶栏已有「返回选角再进入」；发放成功提示不要和这个相反
- 未选角色：先 toast「请先选择角色」，不要打出 `/api/characters/undefined/...`

### 筛选与页签

- 切角色后页码回到 0，筛选不要把 A 的背包页画到 B
- 发放浏览请求带当前筛选签名；过期响应丢弃
- 邮箱展开集合随角色清空，不要把上一角色的展开行留在新列表

---

## 性能

- **物品浏览**走 `/api/items/browse?limit&offset`。`ItemPageSize` 只允许 10/15/20/25。禁止一次把 PVF 全量物品塞进 DOM 或一次 `limit` 上千
- **角色背包 / 邮箱 / 金库**可以一次拉当前角色快照再客户端分页；禁止按行再打 `grant-options` / 附件详情
- **任务搜索**可较大 limit，但结果仍渲染当前页；不要为搜索去全量 `innerHTML` 几万行
- **PVF**：磁盘索引（`PvfDiskIndexStore`）。不要为了前端快把全量 `ItemEntry` 常驻内存
- **异常清理**是全账号扫描。UI 必须能表示 running；后端不要在合法 ID 空集时开扫
- **迁移**是停服级。不要轮询打爆 API；一次提交 + 结果面板
- 大表用现有 `.table-scroll` + 分页。新增列表默认跟发放/背包同一套页大小
- 切角色时作废中的请求即可，不必上 AbortController；但新响应不得写 DOM
- 不要为「顺手优化」改 mmap / 索引文件格式 / 全表扫 SQL

---

## 审查怎么走（缺一项标未查）

1. `index.html` 页签、顶栏危险按钮、script 顺序、`bindings.js` 是否漏绑
2. 四主题下新加的硬编码色、未 `escapeHtml` 的 `innerHTML`
3. `selectCharacter` / 每个 `load*`：epoch + characterId
4. 每个写库按钮：busy、confirm 文案、失败 toast
5. 发放浏览与背包/任务/邮箱列表：分页、过期响应、切角色复位
6. 异常清理 / 迁移 / 备份恢复：范围文案是否等于真实 API（全账号 vs 当前角色）
7. 列表 API：`limit/offset` 是否被前端绕过；PVF 查询是否全表进内存

严重级：

- **Critical：** 切角色后按钮打到新角色；飞行中双发写库；确认文案说当前角色、API 却打全账号；一次渲染/查询拖死浏览器或进程
- **Important：** busy 漏解、空态缺失导致误点、分页无效、硬编码色在某主题不可读、未 escapeHtml
- **Minor：** 文案、间距、toast 时长、计数不准但不导致误操作
