# Repository Guidelines

## 项目结构

- `Program.cs` 是 ASP.NET Minimal API 入口；`GmConfig.cs` 和 `GmToolHostConfig.cs` 负责数据源与运行配置。
- `Services/` 存放账号、角色、背包、任务及 PVF 索引业务；`ServerCore/` 保存与服务端一致的业务模型和 SQLite 逻辑。
- `PvfLib/` 是独立的 PVF 解析库，`SelfTests/` 是面向关键业务流程的自测入口。
- `wwwroot/` 使用原生 HTML/JS/CSS；`Pic/` 存放 README 图片；`docs/` 存放同步流程和状态记录。不要提交 `bin/`、`obj/` 或真实服务端数据。

## Agent 作业入口

- 选作业类型、复制短指令：[`docs/AGENT_TASKS.md`](docs/AGENT_TASKS.md)（86JPGMTool 同步 / 磁盘树移植 / 本仓库功能 / 全仓库审查 / UI 审查 / 增量审查）。一次只做一种。
- 发放、背包、邮件、异常清理、账号备份：先读 [`docs/INVARIANTS.md`](docs/INVARIANTS.md)。
- UI、交互、列表性能：先读 [`docs/UX.md`](docs/UX.md)。

## 构建、运行与发布

项目使用 .NET 10 SDK：

```bash
dotnet restore
dotnet build DfoGmTool.csproj -c Debug
dotnet run
dotnet publish DfoGmTool.csproj -c Release -r win-x64 --self-contained true -o bin/publish
```

开发运行后访问 `http://localhost:5050`。运行前应准备服务端 `inventory.db` 和 `Script.pvf`，或使用 `--server-bin <路径>` 指定服务端目录。

## 测试指南

仓库目前没有独立的测试项目，使用临时 SQLite/PVF 数据执行自测：

```bash
dotnet run -- --selftest-item-grant-options
dotnet run -- --selftest-character-mutations
dotnet run -- --selftest-inventory-migration
dotnet run -- --selftest-mailbox-gm
```

修改物品、角色、邮件或迁移逻辑时运行对应自测；没有统一覆盖率门槛。迁移场景必须确认事务回滚、满包残余和镜像清理行为。`--selftest-character-mutations` 需要本机 `Script.pvf` 或 `PVF_ARCHIVE_PATH`；没有就在报告里写环境缺口，不要标通过。

## 编码规范

遵循 `.editorconfig`：C# 使用 4 个空格，JSON、项目文件及前端文件使用 2 个空格，UTF-8、CRLF 和文件末尾换行。类型与公开成员使用 `PascalCase`，局部变量和参数使用 `camelCase`，私有字段使用 `_camelCase`。按现有模式拆分 `GmService.*.cs` 等 partial 文件；路由留在 `Program.cs`，业务逻辑放入 `Services/`。

## 提交与 Pull Request

Git 历史采用 Conventional Commits 风格，例如 `feat(mailbox): 支持自定义装备邮件发放功能`、`refactor(ItemGrantOptions): ...` 和 `chore: ...`。提交标题应简短并说明行为变化。PR 需要包含变更摘要、验证过的命令及结果；涉及 `wwwroot/` 时附界面截图，涉及数据库或迁移时说明数据影响、备份和停服要求。

## 配置与安全

不要提交密码、真实 `config.ini`、`inventory.db` 或 `Script.pvf`。修改运行数据前先备份数据库；执行背包迁移前停止游戏服务端并确保没有在线角色。远程模式使用 HTTP，仅应配合防火墙、VPN、SSH 隧道或 HTTPS 反向代理。
