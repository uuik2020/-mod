# AI雇工 (AI Hireling) — 源码目录

这个文件夹只放 Mod 源码（`net6.0` / SMAPI 4.x）。**文档统一在仓库根目录维护**，避免两份说明不同步：

- [../README.md](../README.md) —— 功能特性、安装、三种配置方式、源码结构与改造指引
- [../PROJECT.md](../PROJECT.md) —— 架构决策、每个 bug 的根因分析、踩过的坑

## 编译

```bash
dotnet build AIHireling.csproj -c Release
```

`ModBuildConfig` 会自动把 dll / manifest / assets 部署到游戏 `Mods\AIHireling\`。
如果游戏正开着（dll 被占用），加 `-p:EnableModDeploy=false` 只编译不部署，再手动替换。

## 文件速查

| 文件 | 作用 |
|------|------|
| `ModEntry.cs` | 入口：事件、设置菜单、雇佣/工资/对话、存档流程 |
| `Companion.cs` | AI 本体：决策状态机、移动寻路、各项干活逻辑 |
| `CompanionRenderer.cs` | 世界渲染：精灵、影子、头顶 HUD、钓鱼特效、遮挡处理 |
| `DrawHook.cs` | Harmony 补丁，把身体插进原版 `drawFarmers` 之后 |
| `ConsoleArt.cs` | 控制台彩色 + 图标（`CLog` 快捷接口） |
| `DialogueSystem.cs` | 自绘底部对话框 |
| `NativeSave.cs` | 随时存档（驱动原版 `SaveGame.Save()` 协程） |
| `ModConfig.cs` / `SaveData.cs` | 配置项 / 存档结构 |
| `assets/ai_sprite.png` | 12 帧小人，由 `../tools/make_sprite.py` 生成 |
