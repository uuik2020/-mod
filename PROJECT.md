# AI雇工 Mod — 项目维护文档

> 本文档面向后续维护者（包括几个月后的自己）。
> 玩家向的说明在 `AIHireling/README.md` 和分发包里的 `使用说明.txt`；
> 本文只讲**怎么改、怎么构建、怎么发布、哪里埋着坑**。

---

## 1. 项目概览

《星露谷物语》(Stardew Valley 1.6.15) SMAPI Mod：可雇佣的 AI 雇工。
开局出现在农舍门外，对话免费打工 2 天后按日收费；能跟随、打怪、
收获、浇水、翻地、播种、采集、砍树、挖矿、钓鱼、农场清理。
死亡后在家门口重生，重新雇佣涨价。参数调整：对话 →「设置（调参数）」内置 5 页菜单（2.1.0 起加了「存档与快捷键」页），
另可选装 GMCM 1.16+ 用完整 8 页编辑。

- 当前版本：**2.1.1**
- 技术栈：C# / net6.0 / SMAPI 4.x / ModBuildConfig 4.0.1
- 依赖：SMAPI 4.x（**不再依赖 GMCM**；可选装 GMCM 1.16+ 拿完整 8 页编辑界面）

## 2. 环境与构建

### 2.1 构建 + 自动部署

```bash
cd stardew-mod/AIHireling
dotnet build -v q
```

ModBuildConfig 会**自动把 bin/Debug 部署到游戏 Mods 目录**
（`E:\SteamLibrary\steamapps\common\Stardew Valley\Mods\AIHireling\`），
不需要手动拷贝。注意它连 `AIHireling.pdb` 也会部署——**打包分发前必须剔除**。

### 2.2 部署验证三板斧（每次改完必做）

```bash
# 1. MD5 对比：确认部署的就是刚编译的
md5sum AIHireling/bin/Debug/AIHireling.dll \
      "E:/SteamLibrary/steamapps/common/Stardew Valley/Mods/AIHireling/AIHireling.dll"

# 2. 字符串验证：确认新文案真编进 dll 了
#    注意：.NET 字符串在 dll 里是 UTF-16LE，grep 按 UTF-8 找不到！
python -c "
data = open('AIHireling/bin/Debug/AIHireling.dll','rb').read()
for s in ['农场清理','清理中']:
    print('OK' if s.encode('utf-16-le') in data else 'MISS', s)"

# 3. 游戏日志：%AppData%/Roaming/StardewValley/ErrorLogs/SMAPI-latest.txt
```

### 2.3 打包分发

```bash
# 1. 建 staging 目录（结构必须是 Mods/ 在根，用户解压后合并进游戏目录）
# 2. 剔除 *.pdb
# 3. PowerShell 压缩（不要在 Bash 里调 powershell，会被安全策略拦）
Compress-Archive -Path "dist/AI雇工Mod-x.y.z/*" -DestinationPath "dist/AI雇工Mod-x.y.z.zip" -Force
# 4. Python zipfile 校验 CRC + 条目 + dll MD5
```

历史分发包在 `stardew-mod/dist/`。**版本号三处必须同步**：
`manifest.json`、`AIHireling.csproj` 的 `<Version>`、`使用说明.txt` 的更新记录。

分发包结构（2.3.1 起 `使用说明.txt` 放在 Mod 文件夹里，不再放 zip 根目录）：

```
AI雇工Mod-x.y.z/
└── Mods/AIHireling/
    ├── AIHireling.dll
    ├── manifest.json
    ├── 使用说明.txt      ← 玩家解压后直接能在 mod 文件夹里看到
    └── assets/ai_sprite.png
```

> SMAPI 只认 `manifest.json` 指向的 dll，mod 文件夹里多一个 txt 不影响加载。

#### 小黑盒（Heybox）适配器规则

平台给的安装器脚本（`index.cjs`）里，SMAPI 类 mod 走的是这个函数：

```js
function installStardewValley(files, stagingPath) {
  const manifestFiles = files.filter(isManifest);          // 找所有 manifest.json
  for (const manifestFile of manifestFiles) {
    const rootFolder = posix.dirname(relManifest);         // ← 取 manifest 的父目录
    const modName = sanitizeName(rootFolder ? basename(rootFolder) : manifest.Name);
    const prefix = rootFolder ? `${rootFolder}/` : "";
    for (const source of files) {
      if (!source.startsWith(prefix)) continue;
      instructions.push({ copy, source, destination: `${modName}/${source.slice(prefix.length)}` });
    }
  }
  return { instructions, modType: "SMAPI" };              // SMAPI 类型 → 装到 {game}/Mods/
}
```

**结论：安装文件夹名取自 manifest 的父目录名，不是 manifest 里的 `Name`。** 于是：

| 包结构 | 装到哪 |
|--------|--------|
| `AIHireling/manifest.json`（扁平） | `Mods/AIHireling/...` ✅ 推荐 |
| `Mods/AIHireling/manifest.json`（当前） | `Mods/AIHireling/...` ✅ 也对（`basename` 正好取到 `AIHireling`） |
| `manifest.json` 直接在 zip 根 | `Mods/AI雇工 (AI Hireling)/...` ⚠️ 用 `Name` 当文件夹名，带空格括号，丑且和 UniqueID 脱节 |

**所以永远不要把 `manifest.json` 放在 zip 根目录。** 两种有父目录的结构都能正确安装，扁平版更标准（也是 Nexus 上多数 mod 的形态）。

其他判定：

- `testSupported` 要求「有 manifest.json」且「没有 `content/` 目录」且「不是 SMAPI 安装包」→ 我们命中通用安装器。
- `isManifest` 会跳过 `i18n/` `locale/` `locales/` 下的 manifest，别把翻译包塞进这些目录。
- zip 里如果有**多个** manifest 会装成多个 mod，所以打包时别把 `_refs/old-aihireling` 之类带进去。
- 适配器本身能装 SMAPI（识别 `smapi.installer.dll` / `StardewModdingAPI.exe`，还会写 Steam 启动项 `"<smapi.exe>" %command%`），
  并且 `getSmapiStatus()` 会检查游戏根目录有没有 `StardewModdingAPI.exe`，缺了会提示装 SMAPI。

发布用的两个包：

- `dist/AI雇工Mod-2.7.1.zip` —— 手动安装用（解压合并 `Mods` 文件夹，说明书写的是这个流程）
- `dist/AIhireling-2.7.1-适配器版.zip` —— 适配器/一键安装用（zip 根就是 `AIHireling/`）

## 3. 目录结构

```
stardew-mod/
├── AIHireling/              ← Mod 源码（唯一需要动的目录）
│   ├── AIHireling.csproj    # net6.0 + ModBuildConfig 4.0.1，改版本号在这
│   ├── manifest.json        # SMAPI 清单，改版本号在这
│   ├── ModEntry.cs          # 入口：事件接线、GMCM 注册（8 页）、雇佣/工资/对话流程
│   ├── ModConfig.cs         # 全部配置项，中文键名；Sanitize() 防手改炸配置
│   ├── SaveData.cs          # 存档持久化（Helper.Data，键 "aihireling"）
│   ├── NativeSave.cs        # 随时存档：驱动原版 SaveGame.Save() 协程（见 §4.9）
│   ├── Companion.cs         # AI 实体：状态机 + 移动/战斗/农活/清理
│   ├── CompanionRenderer.cs # 世界渲染：精灵/血条/头顶标签/钓鱼线
│   ├── DialogueSystem.cs    # 自绘对话 UI（IClickableMenu）
│   ├── IGenericModConfigMenuApi.cs  # GMCM API 接口（反射调用）
│   └── assets/ai_sprite.png # 192x24 十二帧精灵图（4 方向 x 待机/走1/走2）
├── tools/
│   ├── make_sprite.py       # 像素贴图生成器（改形象/加帧改这里，重跑即可）
│   └── list_types.py        # .NET 元数据解析器（查游戏 dll 里的类型；见 §7.3）
├── _tools/reflect/          # MetadataLoadContext 反射小工具（查游戏 dll 成员/可见性，见 §7.2.1）
├── gmcm-src/                # GenericModConfigMenu 1.11.2 源码（仅存档参考；1.11.2 在 1.6 上不显示图标，打包已不再附带）
├── _refs/stardewmods/       # 参考资料
└── dist/                    # 历史分发包（zip + staging 目录）
```

## 4. 架构：为什么这样设计

### 4.1 AI 不是 NPC

`Companion` **不注册进 `GameLocation.characters`**——否则会被游戏的
NPC AI/日程系统接管，乱跑乱说话。它是纯数据+逻辑实体：

- **驱动**：`ModEntry.OnUpdateTicked` 每帧调 `Companion.Update(dtMs)`
- **绘制**：`ModEntry.OnRenderedWorld` 调 `CompanionRenderer.DrawWorld`
- **移动**：直接插值 `Position`，碰撞用 `loc.isCollidingPosition()`

### 4.2 跨地图守卫（最重要的不变量）

`Companion.Location` 记录 AI 所在 `GameLocation`。**所有移动/思考/绘制
都必须先过 `Location == Game1.currentLocation` 检查**。裸像素坐标
跨地图是没有意义的（还会穿墙——历史上 AI 追进屋 bug 的根因）。

```csharp
if (Location != null && Game1.currentLocation != Location) return;
```

换地图时的规则（`ModEntry.OnWarped`）：
- 玩家进屋（FarmHouse/Cabin）→ AI 传送回 `HomeTile`，`Location = Farm`
- 玩家去别的地图 → AI 传送跟随，`Location = e.NewLocation`

### 4.3 任务状态机

`Think()` 每 350ms 决策一次，产出 `Job`；`ExecuteJob()` 每帧执行：

```
Think() 决策优先级（自动模式）：
  夜晚且开启自动回家 → 回家
  打怪（保命，所有模式都查）
  待命/跟随 → 直接返回
  农场清理（专用模式只干这个；自动模式是兜底）
  农活：收获 > 浇水 > 播种 > 翻地（需要玩家在 workR 内）
  采集（掉落物 + 机器成品）
  砍树
  [自动] 农场清理兜底
  钓鱼
```

`JobType`：无/回家/打怪/挖矿/砍树/钓鱼/采集/收获/浇水/翻地/播种/清理。
每个 `Do*` 方法的结构都一样：**目标失效就弃任务 → 远了就 MoveToward →
近了就冷却计时+播放打击动画 → 执行原版逻辑**。新增工作类型照抄这个模子。

**多段动作（要打两下的）必须继承进度**：`Think()` 开头会 `Job = new Job()`，
如果每次重新决策都把 `Job.Progress` 清零，而打击间隔（`攻击间隔毫秒`，默认 700ms）
大于决策间隔（350ms），第二下永远打不到，表现就是「显示清理中/挖矿中但什么都不发生」。
`Update()` 里用 previous Job 做同 Type + 同 Tile 的进度继承（v2.0.6 修复）。

### 4.4 AI 背包与领取

> 背包满了自动跑来找玩家的流程见 §4.12。

清理/采集到的东西可以先进 AI 背包，再由玩家免费取走：

- `Companion.Inventory`（`List<Item>`）内存态，`AddToInventory(itemId, count)`
  按 `maximumStackSize()` 堆叠
- 农场清理：树枝 `294`、石头 `390`、树桩木头 `388` 进背包（杂草/枯苗无掉落）
- 对话主菜单在有货时动态插入「领取物品（N 组）」→ 物品列表页 →「全部拿走（免费）」
  → `Game1.player.addItemToInventory`，满了`Debris` 掉脚边
- 持久化：`SaveData.背包物品`（`List<StoredItem>`，只存 ItemId + Stack），
  `SaveDataToDisk()` 里由 `SyncInventoryToData()` 镜像，读档时 `LoadCompanionInventory()` 重建。
  **不要直接序列化 `Item`**
- `Die()` 里先 `DropInventory()` 再 `SaveDataToDisk()`——顺序反了会让掉出去的物品
  还留在存档里，读档复制一份

`UpdateStuck()`：位置 2.5 秒没动就放弃当前任务（防卡死）。
同时计算 `WasMoving`（真实位移）供渲染器切行走帧。

### 4.5 对话系统（自绘 UI + 每帧兜底）

`DialogueSystem` 挂一个空白 `BlankMenu` 到 `Game1.activeClickableMenu`
阻挡游戏输入，自己画底部对话框。已知的坑和机制：

- **菜单开着时 SMAPI 的 ButtonPressed 仍然触发**（实测确认），所以对话里
  按 E 是能选选项的
- **游戏侧关菜单不会同步我们的状态**（ESC、传送、读档都可能）→
  `SyncMenuState()` 每帧掰正：对话开着菜单被关→关对话；对话关了菜单
  还挂着→立刻摘除。这是 WASD 卡死 bug 的修复核心，**别删**
- 有外来菜单抢占时直接放弃本次对话（`Open()` 开头的守卫）
- 选项页：方向键随时移动光标，E/回车/左键确认，Esc/N/右键取消
- 选项多时对话框自动增高：`height = Math.Max(230, 110 + optionsH)`
- **中文字体（SmallFont/dialogueFont）没有 `↑↓▶` 字形**，会渲染成
  `**`/`*` 乱码。UI 里只用文字描述和全角 `》`

### 4.6 渲染层级（CompanionRenderer.DrawWorld）

自下而上：影子 → 精灵（16x24 @4x）→ 钓鱼水面涟漪 → 鱼线 → 咬钩水花 →
鱼竿/浮漂 → 状态胶囊（-94px）→ 名字（-50px，dialogueFont）→ 血条 →
脚下「（模式）　背包 N」（+74px）→ 交互提示（+94px）。

**头顶 HUD 的垂直预算**（2.3.1 重排，别再随手加行）：

| 元素 | 位置 | 高度 |
|------|------|------|
| 状态胶囊 `DrawStatusChip()` | -94 | 小字体 27 + 8（内边距）+ 5（进度条） |
| 名字 | -50 | dialogueFont 约 46 |
| 血条 | 名字 + 24 | 7 |
| 脚下：模式 + 背包 | +74 | 小字体 27 |
| `[E] 对话`（仅靠近时） | +94 | 小字体 27 |

- 精灵本体占 -32 ~ +64（16x24 @4x = 64x96）。
- **小字体 27px、对话字体 46px**——`-74 / -46` 这种间距必然重叠（2.3.0 及以前这 5 行字糊
  在脑袋上就是这么来的）。加新行前先算：相邻两行的中心距 ≥ (上行高 + 下行高) / 2。
- 「只是跟着你」时 `ActionLabel()` 返回空串 → 不画胶囊，避免头顶常驻一个框。
- 钓鱼时进度条**画在胶囊内部底部**，不再单独占一条。

精灵图帧布局（`FrameFor(facing, walkPhase)`）：

| facing | 0=上 | 1=右 | 2=下 | 3=左 |
|--------|------|------|------|------|
| 帧区间 | 9-11 | 6-8  | 0-2  | 3-5  |

组内 +walkPhase（0=待机 1=走1 2=走2）。行走相位
`1 + (WalkAnimTime/140) % 2`，仅 `IsWalking`（真实位移）时播放。

**改形象**：编辑 `tools/make_sprite.py` 的调色板和部件函数后重跑，
会重新生成 `assets/ai_sprite.png`（纯 Python 写 PNG：RGBA + filter 0 +
zlib level 6，MonoGame 兼容）。

### 4.7 寻路（2.0.8）

旧版只有"直走 + 单轴滑动"，被树/围栏挡住就原地不动；`UpdateStuck` 2.5 秒后
放弃任务，但下一次 `Think()` 又选中同一个最近目标 → **无限死磕**，玩家看到
的就是"卡在原地、头顶还写着清理中"。

现在的三层策略（都在 `Companion.MoveToward`）：

1. **直线 + 轴向滑动**（快路径，绝大多数情况下够用）
2. **网格 BFS 绕路**：直线走不通时，以「走到目标相邻格」为终点，在 ±16 格内
   BFS（节点上限 240，600ms 最多重算一次）。可走性用与移动**同一个**
   `isCollidingPosition` 盒（48x48 @ +8/+16），保证"规划能走 = 实际能走"
3. **拉黑 + 脱困**：`_stuckTimer` 超 2.5 秒 → `Avoid(Job.Tile)` 黑 60 秒，
   目标搜索（清理/挖矿/砍树）统一跳过黑名单；若 2.5 秒一步都没挪动且当前格
   本身就不可站（被机器/新树压住）→ `FindNearestStandable` 挪到最近空位
4. **卡住就朝目标挪一步（2.2.2，最关键的兜底）**：`ApproachTimeout()` 发现
   2.5 秒零进展（`_approachBest` 不再变小）→ `HopToward(target, 2)` 找一个
   「`CanStandAt` 且更靠近目标」的格子直接 `Position = tile*64` 挪过去，最多连挪 3 次。
   砍树/挖矿/清理/采集/钓鱼共用这一条。

**为什么必须有第 4 条**：AI 贴着房子/围栏时，48x48 碰撞盒和墙一直重叠，
每帧只能抖几像素——`WasMoving`（>2px）和 `Tile` 都在变，**所有"有没有动/有没有换格"
的判据都会失效**（2.2.0 卡 11 秒、2.2.1 按格子判定仍失效，都是这个原因）。
卡死兜底只能直接**改变位置**，不能继续调超时。

**BFS 预算**：2.1.3 起是 800 节点 / ±22 格 / 600ms 一次（240 只够半径 7~8 格，
农场农舍到池塘那种 10+ 格且隔着房子的目标永远找不到路）。

### 4.8 手持工具（2.0.8 加入，2.0.17/2.0.19 全删）

> **现状**：人物身上不再绘制任何工具/特效。玩家两次反馈"人物身上有白条"，先删了
> `Tool.drawInMenu` 的工具图标（2.0.17），又把挥击弧光从白矩形改成细痕（2.0.18）、
> 最后整段删掉（2.0.19）。**以后别再用 `_pixel` 往角色身上叠东西**，动作反馈靠头顶标签。
>
> 保留下来的只有 `GetTool` / `ToolMatches` / `FindTool`（`DoChop` 走原版
> `tree.performToolAction` 需要真的 `Tool` 对象），匹配顺序：类型名 → ItemId → DisplayName。

原本的设计（历史记录）——干活时按任务选工具贴图：

| 任务 | 工具 |
|------|------|
| 砍树 / 树桩 / 树枝(294) | 斧头 `Axe` |
| 挖矿 / 石头(390) | 镐子 `Pickaxe` |
| 杂草 / 收获 | 镰刀 `Scythe` |
| 枯苗 / 翻地 | 锄头 `Hoe` |
| 浇水 | 喷壶 `Watering Can` |

贴图来源 `Game1.toolSpriteSheet` + `Tool.IndexOfMenuItemView * 16`。
**优先取玩家背包里同 ID 的那把工具**（`ToolMatches` 用 `ItemId.EndsWith` 匹配），
这样升级过的金斧头/铱镐子贴图会自动跟着变；玩家把工具放箱子里时才
`ItemRegistry.Create("(T)" + id)` 造一把同款（失败就静默不画，不抛异常）。
挥动时按 `AttackAnimPhase` 在朝向对应的弧线上旋转。

### 4.9 随时存档（2.1.0）

> 存档的「只在睡觉时间能存」开关见 §4.13。

玩家要求「加个原生保存功能」→ 不自己造存档格式，而是**把原版 `SaveGame.Save()` 协程原样跑一遍**。

原版只在夜里（`Game1.NewDay` → `SaveGameMenu`）跑这段协程，白天想跑就得自己搭台：

```
TrySave()                                     Tick()（每帧）
  ├ 守卫：单人 / gameMode==3 / 无剧情 / 无节日 / 无菜单
  ├ SaveDataToDisk()      ← AI 状态先进 ModData，才会被写进这次存档
  ├ BackupSaveFolder()    ← 每天第一次：当天开局存档 → Saves\_AIHireling备份\
  ├ Game1.game1.IsSaving = true   ← 原版标志：冻结世界 + 屏幕切纯色 + 只画菜单
  ├ _routine = SaveGame.Save()
  └ 挂 SaveProgressMenu（空白菜单，吃输入 + 画「正在存档」）
                                              ├ 挂掉了就重新挂
                                              ├ _routine.MoveNext()
                                              ├ Current>=100 / MoveNext()==false → Finish(true)
                                              └ 60 秒没完成 → Finish(false) 强制退出
```

**关键坑：协程必须靠外部推，而存档时 `Game1.Update` 会提前 return**

```csharp
if (IsSaving) { PushUIMode(); activeClickableMenu?.update(gameTime); ...; return; }   // Game1.Update
```

所以推进点同时挂在 `UpdateTicked` **和** `Rendered` 上（绘制循环一定在跑，存档画面本身就是画出来的）。
只挂 `UpdateTicked` 有卡死风险——那正是《Save Anywhere》历史上"卡在存档界面"的成因之一。

**为什么挡掉联机**：`SaveGame.Save()` 的单人分支走 `getSaveEnumerator()` + `Game1.hooks.StartTask`，
联机分支要先过 `newDaySync.readyForSave()` / `endOfNightStatus` 的 ready 同步；
在白天硬跑联机分支会永远等不到房客 ready。直接拒绝比冒险卡死好。

**为什么挡掉节日**：`getSaveEnumerator` 会先 `location.cleanupBeforeSave()`，它会移除
Junimo / 女巫小屋 NPC / `Tent`；节日中途存会让场景物件当场消失。

**副作用清单**（已确认可接受）：`UnapplyAllTrinketEffects`→`resetAllTrinketEffects`、
`cleanupBeforeSave`、`Game1.player.saveTime` 更新、`_old` 备份轮转。均为原版存档固有行为。

**白天存档的固有代价**：原版只在夜里写盘，白天写出来的存档读回去时，
村民的日程路径可能对不上（表现为发呆/穿墙）。故默认开启「存档前自动备份」，
留一份当天开局的干净存档兜底。

### 4.10 钓鱼的物品来源（2.1.1）

**事故**：玩家截图里背包出现「错误物品（754）」+ 屏幕红色报错。日志显示
`[钓鱼] 钓到 1 条 754` —— 这不是"取不到物品"，而是**取到了一个不存在的 ID**。

```csharp
ItemRegistry.Create("(O)754", 1)   // 754 不在 Data/Objects 里
// → 不抛异常，返回一个 Error Item：名字「错误物品（754）」、图标是禁止符号
// → addItemToInventory 照常收下，于是玩家背包多一格删不掉的东西
```

根因是「可钓鱼类列表」手写死的，里面混了 750~756、887~889 这类凭记忆编的 ID。
**教训：任何"按 ID 造物品"的代码都必须先确认 ID 真实存在。**

现在 `Companion.PickCatch()` 三级取鱼：

```
1. 配置点名的 ID        PickConfiguredFishIds()  失效 ID 就地剔除 + 写回 config.json
2. 原版 getFish         TryVanillaFish()         地图/季节/天气/水深/钓鱼等级全对
   ├ 鱼塘格（FishPond.isTileFishable）直接跳过    ← 原版会 CatchFish() 扣掉玩家养的鱼
   └ 捞到 junkCategory（-20）杂物 → 再抛一竿，两次都是杂物才放弃
3. Data/Objects 鱼表    FishPool()               Category == Object.FishCategory(-4)
```

校验工具（`Companion`）：

| 方法 | 作用 |
|------|------|
| `IsRealItemId(id)` | ID 是否在 `Game1.objectData` 里 |
| `IsBrokenItem(item)` | 纯数字 ID + 表里查不到 = Error Item；非数字 ID（模组注册的）不判坏 |
| `FishPool()` | 鱼表缓存，空结果不缓存（`Game1.objectData` 还没加载完时不锁死） |

### 4.11 钓鱼流程与动画（2.1.2）

**事故**（玩家反馈）：钓鱼时头顶**一直显示「钓鱼中」**，而且**完全看不到动画**。
日志里是同一个目标每 6 秒重复一次：

```
[寻路] 6 秒靠不近 (27,21)，拉黑目标 (28,21) 换活   ← 每 6 秒一模一样
```

三个原因叠在一起：

1. `FindStandTileNear()` 按 `+X → -X → +Y → -Y` 的**固定顺序**取第一个"非水格"，
   不判断能不能站（没有 `CanStandAt`）。海滩潮池那种被礁石围住的格子会被选中，
   AI 一辈子走不进去 → 6 秒超时 → `ApproachTimeout` 拉黑 → 下一次决策**又选回同一格**
   （因为 `FindWaterTile()` 只认 `isWaterTile`，**完全不看黑名单**）→ 死循环。
2. `Think()` 每 350ms 重置一次 `Job`，`Job.Tile` 被重算成"离自己最近的水格"，
   抛竿途中目标会漂移。
3. 所谓"动画"只有一根从头顶连到水面的白线 + 一个白方块，且 `FishStartedAt < 0`
   时连这根线都不画（没走到岸边就什么都没有）→ 玩家看到的就是站着不动 + 钓鱼中。

现在（`Companion`）：

```
Think()  钓鱼分支：Phase != 无 且 FishTile 有效 → Job.Type = 钓鱼 / Job.Tile = FishTile（锁死）
DoFishing():
  走去水边  Phase=走去水边   MoveToward(岸边站位)；6 秒靠不近 → Avoid(水格 + 岸格) + ResetFishing()
  抛竿      Phase=抛竿       420ms；播 "cast"
  等待      Phase=等待       Config.钓鱼等待毫秒；到点 PickCatch() 先算好结果
  咬钩      Phase=咬钩       450ms；播 "fishBite"
  收竿      Phase=收竿       350ms；结束调 FinishFishing() → GiveItem / 空军感慨
```

| 选点 | 规则 |
|------|------|
| `FindWaterTile(radius)` | 由近到远一圈圈扫，取最近一圈里离自己最近的水格；跳过 `IsAvoided`；要求 `HasLandNeighbor()`（八邻有一格不是水） |
| 走到哪下竿 | **不预挑岸边站位**（2.1.2 的 `FindStandTileNear/IsGoodShore` 已删除）。朝水格 `MoveToward`，`Vector2.Distance(Tile, water) <= 4.5` 就抛竿（2.2.4 起放宽，因为目标点会被推进水里 2~3 格） |
| `DeepenWaterTile(water, from)` | 沿 AI→水的主轴连续推最多 3 格，直到 `IsInteriorWater()`（四邻全水）。**`isWaterTile` 为真 ≠ 贴图是水**：岸边 1~2 格在数据里算水，但贴图画着沙滩，浮漂落那儿看着像扔在岸上（玩家连提两次） |
| `FailFishing(water)` | 走不过去：拉黑水格 3x3 → 歇 8s（连续失败 8/13/18/23/28s 递增）→ `ResetFishing()`；休息期内 `Think` 不接钓鱼，头顶不会再长期挂「去水边」 |
| `ResetFishing()` | 清 `FishTile / Phase / _pendingCatch`；换图、放弃、钓完都走它 |

渲染（`CompanionRenderer.DrawFishing`）按 `Phase` 分阶段：

| 阶段 | 画面 |
|------|------|
| 抛竿 | 竿子从后上方（-95°）在 420ms 内挥到指向水面；浮漂沿抛物线飞出去；人物上浮一顿 |
| 等待 | 竿子轻微抖动；浮漂上下 1.5px；两圈涟漪交替扩散；头顶「等鱼上钩 4.2s」+ 进度条 |
| 咬钩 | 竿子上提 -20°；浮漂下沉 8px；6 道白色水花；标签变红「咬钩！」 |
| 收竿 | 浮漂沿缓动回弹到竿尖 |

鱼竿用 `_pixel` 旋转绘制（不用新贴图）；涟漪 `DrawEllipse()` 用 18 段短线拼椭圆。
**各阶段都会 `_stuckTimer = 0`**，否则连续两竿（~20 秒静立）会被 `UpdateStuck` 判成卡住。

浮漂画在 `FindWaterSpriteTile(FishTile)` 的结果上：当前格不是「四邻全水」就在 3 格半径内
就近换一个纯水面格，保证**鱼钩永远在水里**（逻辑格与绘制格允许差 1~2 格）。

### 4.15 钓鱼的花费：鱼竿 / 鱼饵 / 体力（2.4.0）

玩家反馈「AI 钓鱼现在没有任何花费，太作弊了」。以前 `PickCatch()` 站 9 秒就凭空造一条鱼，
既不用竿、不耗饵、也不掉体力。现在照原版补齐三样，各自可关：

| 花费 | 规则 | 实现 |
|------|------|------|
| 鱼竿 | 必须有 `FishingRod`：先找 AI 背包，再找玩家背包 | `FindFishingRod()`；没有 → `FishingBlockReason()` 返回 `"没鱼竿"` |
| 鱼饵 | 每竿扣 1 个，先 AI 背包后玩家背包 | `TryConsumeBait()`；`Object.baitCategory`（-21，各种鱼饵通吃）；没饵 → 咬钩 ×1.9 |
| 体力 | 每竿扣 `8 - 钓鱼等级*0.1`（同 `FishingRod.cs:408`） | `Companion.Energy` + `CastEnergyCost()`，上限 `AI体力上限`（默认 270） |

- 扣费点在 `BeginCast()`：先 `FishingBlockReason()` 判竿/体力，再扣饵、扣体力，最后才定 `FishWaitMs`。
  付不起就 `WarnFishingBlocked()` + `_fishRestUntil = Clock + 20000`，不抛竿。
- `Think()` 的钓鱼分支一开始就判 `FishingBlockReason()`，付不起直接 return（正常跟随），
  不会像以前那样顶着「去水边」跑一趟又白跑。
- 体力：`SaveData.体力` 持久化；`OnDayStarted` 与重生回满；`RegenStamina()`
  睡觉直接回满、待命 2/s、跟随 1/s、自动模式没活干 0.8/s（都在 `!WasMoving` 时才涨）。
- 咬钩时间：`baseMs × (有饵 ? 0.85 : 1.9) × (0.8~1.2 随机)`。
- 渲染：血条下面一条体力条（满时不画），等待标签没饵时带「（无饵）」。

> 注意：`isBait` 不能用 `item.ItemId == "685"`——野生鱼饵/万能鱼饵/模组鱼饵都不是 685，
> 统一用 `Category == Object.baitCategory` 判断。

### 4.16 挖矿的花费 + 打开 AI 背包（2.5.0）

**挖矿照原版收费**（2.4.0 只给钓鱼加了，玩家指出挖矿仍白嫖）：

| 花费 | 规则 | 实现 |
|------|------|------|
| 镐子 | 必须有 `Pickaxe`（AI 背包 → 玩家背包） | `FindPickaxe()` / `MiningBlockReason()` |
| 体力 | 每敲一下 `2 - 挖矿等级*0.1`（`Pickaxe.cs:88` 按基础镐算） | `MiningEnergyCost()`，`DoMining` 每挥一次扣 |

- `Think()` 挖矿分支先判 `MiningBlockReason()`：专门挖矿模式直接歇着，自动模式跳过挖矿去干别的。
- **农场清理里的石头（`ItemId == "390"`）同样要镐子 + 体力**，`FindCleanupTarget` 里没工具就跳过石头——
  否则"挖矿要镐子"在自动模式下等于摆设。
- 提示统一走 `WarnBlocked(why)`（原 `WarnFishingBlocked`）：没鱼竿 / 没镐子 / 没体力，45 秒节流。

**打开 AI 背包**（玩家："对话选项里没有打开 AI 物品栏，没法给他东西"）：

- `OpenCompanionInventory()`：把 `Companion.Inventory` 倒进临时 `Chest(items, c.Tile)`，
  再 `chest.ShowMenu()` 打开原版箱界面；`_companionBagOpen` 期间 `OnUpdateTicked` **不调 `Companion.Update`**（冻结 AI）。
- 关闭判定**不能只靠 `exitFunction`**：`Chest.grabItemFromChest/grabItemFromInventory` 每次抓取都会重新 `ShowMenu()`
  建一个新菜单，exitFunction 会丢。改为每帧轮询 `Game1.activeClickableMenu is not ItemGrabMenu` → `CloseCompanionInventory()`。
- `CloseCompanionInventory()` 把箱子里的东西收回 AI 背包并 `SaveDataToDisk()`；
  **工具（`Tool`，注意在 `StardewValley` 命名空间）直接还给玩家**——塞进 AI 背包会在存档里丢升级信息。
- `InventoryGroups` 排除 `Tool`；`HandOverItems` / `StoreBagToChest` 也跳过工具，免得把玩家借的竿/镐顺走。

### 4.17 遣散后自己走回门口（2.5.1）

玩家问「解雇 AI 他也会在房子附近出现吗」。老逻辑：

- `Dismiss()` 只做 `Mode = 待命` + 清 Job，**不改位置**；
- `FollowPlayer()` 第一行是 `if (Game1.player == null || Mode == 待命) return;`，
  于是"未雇佣 → GoHome()"那段永远走不到 → 人被解雇后钉在原地，
  头顶还写着「在门口待命」（其实是原地）。只有进屋（`OnWarped` 传送到 HomeTile）或读档才回门口。

现在：

| 位置 | 改动 |
|------|------|
| `FollowPlayer()` | 先处理 `!IsHired`：**在农场里就 `GoHome()`**（别的图没有"家门口"概念，乱走会走向奇怪坐标）；`Mode == 待命` 的判断挪到后面 |
| `GoHome()` | 新增 `_homeStuckMs`：4 秒零位移就认输把 `Position` 直接设到门口（被树丛/围栏围住时别永远杵在野地） |
| `OnWarped()` | 被遣散的 AI 若被落在别的图（矿洞里解雇），玩家回到农场时一并 `Position = HomeTile * 64f` |
| `Dismiss()` | HUD 提示「会自己走回农舍门口待命」 |
| `ActionLabel()` | 未雇佣时按距离区分「回门口中」（>1.5 格）/「在门口待命」 |

> 教训：`Mode == 待命` 这种"早退"写在函数开头，很容易把后面"未雇佣"的分支整段吃掉。
> 调整状态机时先确认早退条件有没有把别的状态一网打尽。

### 4.18 干不了的提示不再刷屏（2.5.3）

玩家截图：控制台里 `[干活] 干不了（没鱼竿）：体力 270/270，鱼饵 0 个` 每 45 秒一条，连成一屏。

原来只有 `_warnUntil` 一个 45 秒节流，只要一直缺鱼竿就一直喊。现在加"每天次数 + 静默天数"：

| 配置 | 默认 | 说明 |
|------|------|------|
| `提示每天最多次数` | 2 | 同一条提示一天最多说几次；0 = 完全不提示 |
| `提示静默天数` | 3 | 说满后静默几天；0 = 当天不再提示、第二天恢复 |

- 计数落在 `SaveData.提示记录`（`List<HintRecord>`：Key / 天 / 次数 / 静默到，都用 `SaveData.AbsoluteDay()` 的绝对日）。
- `Companion.HintAllowed(why)`：静默期没过直接 false；跨天次数归零；说满则把 `静默到 = 今天 + 静默天数`。
- `WarnBlocked()`：会话内间隔 45s→60s；**只有真的要弹 HUD 时才写 Info 日志**，被静默/冷却时静默退出（这才是控制台刷屏的根源）。
- 三条共用：`没鱼竿` / `没镐子` / `没体力`。
- 设置页「工作行为」与 GMCM「干活受阻提示」分组都能调；`ai_cfg` 多一行「提示」。

> 教训：节流只做"时间间隔"是不够的——玩家可能几十分钟都缺同一样东西，得再加"次数上限 + 静默期"。

### 4.19 全功能巡检（2.6.1）

玩家要求"所有功能检查一遍"。这轮按模块过了一遍，修的问题按严重程度排：

| # | 问题 | 原因 | 修法 |
|---|------|------|------|
| 1 | 小分辨率下设置页顶出屏幕 | 「工作行为」一页塞了 21 行，`Dialogue.Draw` 的 `height = 110 + options*26` → 680px | 拆成 工作行为开关(11) / 花费与体力(6) / 提示与背包(4)，`ShowSettingsMenu()` 改表驱动 |
| 2 | AI 背包 >36 组时物品消失 | `new Chest(items, ...)` 只保留 36 格 | 改成 `Chest.addItem()` 逐个放，装不下的回 AI 背包并提示 |
| 3 | 工具还给玩家时可能丢失 | `addItemToInventory` 的 leftover 被忽略 | leftover 丢到 AI 脚边 |
| 4 | 自动模式砍公共地图的树 | 砍树分支没有地图限制 | 自动模式 `loc is Farm`，专用砍树模式不限 |
| 5 | 钓鱼空军弹对话框卡住玩家 | `FinishFishing` 用 `Dialogue.OpenLine` | 改 HUD 飘字 |
| 6 | 工资结算/重生弹菜单 | `DayEnding` / `DayStarted` 里 `OpenLine` 有干扰日程/存档流程的风险 | 都改 HUD |
| 7 | 「领取物品」点了没反应 | 点击只回了句"要哪个说全部拿走" | 点某行就发那一组（`addItemToInventory` + leftover 回背包） |
| 8 | 砍树被卡住检测打断 | `UpdateStuck` 统一 5 秒宽限，砍树站着挥刀不动 | `JobType.砍树 => 9000ms` |
| 9 | 背包满「先背着」实际 2 分钟 | 代码 `PostponeBagAlert(120000)`，说明写 2 小时 | 改 7,200,000ms |
| 10 | 关掉背包满对话后 8 秒又来问 | `_bagTellCooldown = 8s` | 60 秒 |
| 11 | 清理时每个物件都扫一遍背包 | `MiningBlockReason()` 写在 objects 循环里 | 提到循环外算一次 |
| 12 | 4 个 SMAPI 告警 | `buildings` / `Chunks` / `damageToFarmer` / `warps` 的隐式 NetField 转换 | 去掉多余的 `!= null`、改用 `Monster.DamageToFarmer` |
| 13 | 注释错位 | 2.5.0 插入背包段时把 `HandOverItems` 的 summary 顶到了段首 | 归位 |

> 教训：**菜单项用索引式 `switch (case 0/1/2…)` 是 bug 温床**——这轮的模式菜单、设置目录都改成了表驱动。
> 另一个隐蔽点：`new Chest(list, ...)` 这种"一把塞"在容量不足时会静默截断，凡是搬运物品都得检查 leftover。

### 4.20 控制台彩色 + 图标（2.6.2）

SMAPI 控制台的日志默认全是同色白字，几十行 Trace 混在一起分不清。这版给日志上色加图标。

**为什么不能直接塞 ANSI 码**：Windows 控制台默认不解析 ANSI（`ENABLE_VIRTUAL_TERMINAL_PROCESSING` 关闭），
直接输出 `\u001b[38;5;51m` 只会显示成 `←[38;5;51m` 乱码。SMAPI 自己没开这个标志（dll 里搜不到 `SetConsoleMode`），
所以 `ConsoleArt.Init()` 里自己 P/Invoke：

```csharp
GetStdHandle(STD_OUTPUT_HANDLE) → GetConsoleMode → SetConsoleMode(mode | 0x0004)
```

拿不到句柄 / 老 conhost / 非 Windows 时 `AnsiOk = false`，所有颜色自动降级成纯文本，不报错。

**为什么不用 emoji 做图标**：中文 Windows 的 cmd 默认字体（新宋体 / 点阵）没有 emoji 字形，
`🐟` 会变成方块或问号。只用 BMP 里的几何符号，等宽字体全覆盖：

| 图标 | 用途 | 颜色 |
|------|------|------|
| `◆` | 信息 / 加载 | 青 |
| `▶` | 步骤 / 动作 | 蓝 |
| `√` | 成功 | 绿 |
| `▲` | 警告 | 黄 |
| `×` | 错误 | 红 |
| `★` | 重点 / 提示 | 金 |
| `※` | 说明 | 紫 |
| `·` | 追踪（`[钓鱼]` `[寻路]` …） | 青灰 |

**结构**：

- `ConsoleArt`：颜色常量（256 色 SGR）、图标常量、`Init()`、`Banner()`、`Emit/Compose`。
- `CLog`：给业务代码用的短接口（`CLog.Warn(...)` 一行搞定），内部走 `ModEntry.SMonitor`。
- `Compose()`：多行块只有首行带图标，续行补两格缩进对齐（SMAPI 只给一条日志的首行加 `[INFO 模组名]` 前缀）。
- `HighlightTag()`：行首 `[xxx]`（长度 3~11）单独上亮色 + 加粗，正文回到原色。
- 批量替换：65 处 `Monitor.Log(x, LogLevel.Y)` → `CLog.Y(x)`；带 `[标签]` 的 Trace 改用 `Act` 让标签高亮。

> 注意：颜色码会跟着消息一起写进 `SMAPI-latest.txt`，记事本里可能看到 `←[38;5;51m` 之类的控制符。
> 这属于预期行为（grep 文字内容仍然正常）；觉得碍眼就在「设置 → 控制台显示」关掉彩色。

### 4.21 身体要被草和作物挡住（2.6.4）

玩家截图两次：先是"AI 站进草地像贴纸糊在最上层"，改完又变成"整个人被草吞掉，只剩名字飘着"。

**根因是绘制时机**。`Game1` 画世界用 `SpriteSortMode.Deferred`（`Game1.cs:13556`），
**layerDepth 不参与排序**，只按调用顺序决定前后：

| 顺序 | 谁画 | 内容 |
|------|------|------|
| 1 | `GameLocation.drawBackground` | 地块、水、地板、建筑背景、debris |
| 2 | `GameLocation.draw` → `drawCharacters` / `drawFarmers` | 村民、玩家 |
| 3 | 同上，`objects` 循环 | 石头、树枝、杂草、机器、箱子 |
| 4 | `GameLocation.drawAboveFrontLayer` | **terrainFeatures**（草、作物、树、树桩） |
| 5 | `drawAboveAlwaysFrontLayer` | 云影、雨 |
| 6 | SMAPI `Display.RenderedWorld` | 我们（如果在这里画）← 永远在最后 |

**2.6.3 走过的弯路**：既然插不进去，就"画完 AI 再把周围地形补画一遍"。结果同一张草贴图
叠了两层，alpha 从稀疏变成不透明，整个人被埋进草里。**教训：不要靠重复绘制来模拟遮挡。**

**2.6.4 的做法**：用 Harmony 把身体插到第 2 步末尾。

```csharp
[HarmonyPatch(typeof(GameLocation), "drawFarmers")] // Postfix
private static void AfterDrawFarmers(GameLocation __instance, SpriteBatch b)
{
    if (__instance != Game1.currentLocation) return;
    if (ModEntry.Config?.身体被作物遮挡 != true) return;
    if (__instance.shouldHideCharacters()) return;
    if (Game1.eventUp && (Game1.CurrentEvent == null || !Game1.CurrentEvent.showWorldCharacters)) return;
    CompanionRenderer.DrawBody(b, Companion);
}
```

- 位置正好是"玩家刚画完、objects 和地形前景还没画"——AI 享受和玩家一模一样的遮挡，
  而且**每张贴图只画一次**。
- 渲染拆成两个入口：`DrawBody`（影子 / 精灵 / 钓鱼特效，走补丁）与
  `DrawOverlay`（状态胶囊 / 名字 / 血条 / 脚下标签，走 `RenderedWorld`），
  保证 UI 不会被草叶吃掉。
- `DrawHook.Applied == false`（补丁没挂上）或玩家关掉开关时，`RenderedWorld` 里补画身体 →
  退回"永远画在最上层"，功能不会丢。
- `csproj` 增加 `0Harmony` 引用：`$(GamePath)/smapi-internal/0Harmony.dll`，`Private=false`
  （Harmony 由 SMAPI 自带，不能打进 zip）。
- 启动日志会写「身体遮挡已接进原版绘制顺序」，挂不上会写警告。

**2.6.5：再补一层"上半身"**

原版层序接好之后暴露出第二个问题：AI 站在草丛**正中间**（上下左右都是草）时会被盖得只剩一点皮肤，
而玩家站在草丛边缘，露出整个上半身。放大截图对比确认这是**站位差异**，不是绘制错位——
但对玩家来说"看不见自己雇的人"依然不可接受。

所以 `DrawOverlay` 里多画一道 `DrawUpperBody()`：把 `16x24` 帧的**上半 12 像素**（头 + 肩 + 胸）
在 `RenderedWorld`（世界之后）再画一次，压在草叶上面。效果：

- 腿脚仍在草下面 → 和原版一样"站在草里"；
- 上半身从草叶间冒出来 → 永远看得见；
- 切口在胸口（`drawBase + 48px`），正好是草叶最密的位置，看不出接缝。

只在 `身体被作物遮挡 == true` 时补画（关掉时整个身体本来就在最上层，再画会重影）。

**2.6.6：有树就不补画**

补画上半身在草丛里好看，但站在树后面时会**穿模**——脑袋直接从树冠、树干上冒出来（玩家截图）。
所以加一道 `CoveredByTallThing(c)` 门槛：

| 检查 | 范围 |
|------|------|
| `Tree` / `FruitTree` | 身体周围 ±2 列、上方 3 行 ~ 下方 2 行；树冠矩形 `192x192`（`48x48` 贴图 @4x），中心在 `(tx*64+32, ty*64-32)` |
| `Bush`（1.6 的 `largeTerrainFeatures`） | `getBoundingBox()` 与身体矩形求交 |

命中就整段跳过补画，让他老老实实被原版挡住。草丛（`Grass`）不在此列，所以草丛里照样露上半身。

**2.6.7：树后虚化（和玩家一致）**

不补画之后，他站在树后就被完全挡住看不见了。原版的做法是**把树变半透明**：

```csharp
// Tree.updateTick（FruitTree 同套逻辑，阈值 4 段、矩形 192x224）
alpha = Math.Min(1f, alpha + 0.05f);              // 每帧慢慢恢复
if (growthStage >= 5 && !falling && !stump
    && Game1.player.GetBoundingBox().Intersects(new Rectangle(64*(tileX-1), 64*(tileY-5), 192, 288)))
    alpha = Math.Max(0.4f, alpha - 0.09f);        // 玩家在树后 → 虚化到 0.4
```

**只认 `Game1.player`**，NPC/AI 不算。`CompanionRenderer.FadeCoveringTrees()` 用完全相同的几何
（AI 脚下 48x48 碰撞盒 + 同样的树冠矩形）替他跑一遍，命中就 `tree.alpha = Math.Min(alpha, 0.4f)`。

- 时机：`UpdateTicked` 在 `Game1.Update` **之后**触发，树的 update 已经跑完，所以我们压的值
  会被本帧绘制直接用上；下一帧原版 `+0.05` 想恢复，我们再压回去 → 稳定半透明。
- 只处理"成材 + 没倒 + 不是树桩"的树，和原版条件一致，不会让小树苗莫名其妙半透明。
- 跟着「身体被作物遮挡」开关走：关掉时不做任何干预。

**2.6.8：两个补丁**

1. **树虚化放宽到 4 段**。原版只认 `growthStage >= 5`（真正成材的树），
   但 4 段那种"半大树"树冠一样盖得住整个人（玩家截图里正是这种树，死活不虚化）。
   现在 `>= 4` 就算：4 段用 `192x224`（`ty-4`），5 段仍用原版 `192x288`（`ty-5`）。

2. **不许站进建筑占位格**。玩家截图里 AI 站在农舍台阶上，被门廊贴图盖得只剩一个草帽。
   原因是 `isCollidingPosition` 对农舍台阶/门廊那几格是**放行**的（玩家得走到门口按门），
   可建筑的 sprite 覆盖到台阶下面 → 人一站上去就"埋"了。
   新增 `Companion.HasBuildingAt(loc, tile)`（`loc.getBuildingAt(tile) != null`）：
   - `CanStandAt()` 直接排除 → 找目标、站位、脱困都不会选中建筑格；
   - `TryMove()` 也拦（`!HasBuildingAt(Tile) && HasBuildingAt(next)` 才拒绝），
     已经在里面时放行，否则会被锁死走不出来。

**2.6.9：虚化的真正原因（查错方向了）**

2.6.7 写完虚化、2.6.8 还放宽了阈值，玩家反馈"树还是不虚化"。查下来是**遍历范围反了**：

树冠贴图 48x48 @4x 画在根部格子的**上方**，覆盖矩形约 `[ty*64-320, ty*64-32]`。
反解"能盖到人"的树根位置：

```
矩形底   ty*64-32  >= 人体顶 cy*64-32   →  ty >= cy
矩形顶   ty*64-320 <= 人体底 cy*64+64   →  ty <= cy+6
```

也就是树根在人 **同格到下方 6 格** 之间。而代码里写的是 `for (ty = cy-6; ty <= cy+1)`——
往上找，永远找不到。`CoveredByTallThing` 同理，所以"树后不补画上半身"其实从来没生效过。

改 `cy-1 .. cy+7` 之后两件事一起好了：树会虚化、补画也会正确跳过。

**顺手修：跟随模式没有卡住兜底**

`FollowPlayer` 只有一句 `MoveToward(玩家)`。被树/石头围住、BFS 也找不到路时，
`TryMove` 全失败 → 原地站着；`UpdateStuck` 对 `JobType.无` 的宽限是 0，等于完全不管
（玩家截图："为什么卡住了"）。

新增 `_followStuckMs`：连续 2 秒零位移 → `HopToward(玩家格, 2)` 朝玩家挪一格并写
`[跟随] 被挡住了，朝玩家挪一步` 日志，然后继续正常走。

### 4.22 绘制锚点对齐原版（2.7.0）

前面几版一直在"补画 / 压 alpha"上打补丁，玩家要求「按游戏源码改」。查下来**根子在绘制锚点**：

| 谁 | 影子位置 | 脚底 | 出处 |
|----|----------|------|------|
| 原版玩家 | `Position + (32, 24)` | `Position + 48`（`GetBoundingBox() = Position+(8,16)` 高 32） | `Farmer.cs:9199`、`Character.cs` |
| 我们的 AI（旧） | `local + (32, 58)` | `local + 64` | `CompanionRenderer` |

**AI 整个人低了 32px（半格）**。后果全都能对上：

- 站进草丛：身体正好压在草叶最密的那一段（格子中下部），整个人被埋；玩家身体在格子上部（叶尖稀疏处）所以只被盖腿；
- 站到农舍门口：精灵下半截伸进建筑贴图里，看着"站进台阶"；
- 树后虚化判定：脚部盒偏低半格，和树冠矩形的相交关系和玩家不一致。

修法：`CompanionRenderer` 里加常量 `LiftY = 32`，**精灵、影子、名字、状态胶囊、脚下标签、
交互提示、钓鱼手部**统一上提 32px。逻辑坐标（`Position`、`Tile`、碰撞盒）一律不动 ——
只改"画在哪"，不动"站在哪"，所以寻路/碰撞/存档完全不受影响。

### 4.23 背包满了自动找箱子（2.7.1）

玩家反馈：背包满时选了「先背着，等会儿再说」，之后自己造了木箱摆下，AI 不会去放。

原因很直接——旧的 `StoreBagToChest()` 是**对话那一刻**去找箱子：

```csharp
// AlertBagFull() 的选项 2
Chest chest = FindNearestChest(c.Position, 12f * 64f);
if (chest == null) { Dialogue.OpenLine("这附近我没看见箱子……"); return; }
```

当时地上没箱子 → 选项白点 → 之后再怎么摆箱子也没人管。

修法：把"找箱子"变成一个**持续判断的任务**。

| 部件 | 说明 |
|------|------|
| `JobType.存背包` | 新任务类型 |
| `Companion.FindChestForBag(maxTiles)` | 12 格内最近、且没被 `IsAvoided` 拉黑的箱子 |
| `DoStoreBag(dtMs)` | 走到箱子 1.6 格内 → `FaceTowards` → 逐个 `chest.addItem()`；塞不下的留在背包（`RemoveAll(Stack<=0)`） |
| `Think()` | 背包满时**先查箱子**，没有再走"找玩家"那条；`Mode == 待命` 时不去（尊重站着不动） |
| `_bagStoreCooldown` | 塞完 4 秒、箱子满 30 秒、走不到 5 秒，避免每帧重试 |
| `UpdateStuck` | `JobType.存背包 => 9000ms`（站箱子边塞东西别被当成卡住） |
| 渲染 | 头顶标签「背包满了，去塞箱子」 |

- 工具（`Tool`）不往里塞 —— 本来就只是"借玩家的"。
- 箱子被拆/走不到 → `Job.Type = 无` + 冷却，回头找玩家，不会死循环。
- 配置项 `背包满了自动找箱子`（默认开），设置页「提示与背包」与 GMCM 都能关。

### 4.12 AI 背包满了会来找玩家（2.2.0）

- `ModConfig`：`背包容量组数`（默认 12，0=不限）、`背包满了自动找玩家`、`文本_背包满`。
- `Companion.IsBagFull` → `Think()` 里接 `JobType.送背包`（放在「待命/跟随提前 return」
  **之前**，跟随模式下玩家在 3 格内也会开口）→ `DoDeliverBag()`：>1.6 格就提速走过去，
  超过瞬移距离直接传；到面前调 `ModEntry.AlertBagFull()`。
- `AlertBagFull()` 三选一：`HandOverItems()`（全给玩家）/ `StoreBagToChest()`
  （`FindNearestChest` 取 12 格内最近箱子，`Chest.addItem` 塞不下的掉脚边）/
  `PostponeBagAlert(2h)`。
- `_bagTellCooldown` 8 秒防刷屏；头顶胶囊显示「背包满了，去找你」。

### 4.13 存档的睡觉时间限制（2.3.0）

- `ModConfig`：`存档需到睡觉时间`（默认 false）、`文本_存档未到点`。
- `ModEntry.SaveTimeAllowed(out reason)`：`Game1.timeOfDay >= 回家睡觉时间` 才放行；
  否则用 `MinutesOfDay()` + `DurationStr()` 拼出「还差 4 小时 30 分钟」。
- 三个入口都拦：`SaveFromHotkey`（HUD）、`SaveFromDialogue`（对话框）、`ai_cfg save`（日志）。
- 台词：`现在才 {now}，还没到睡觉的点呢——要 {sleep} 才能睡，还差 {left}。等到了我再帮你存。`

### 4.14 头顶 HUD 重排（2.3.1）

玩家反馈「显示都挤到一起了」。原来的 5 行是 `-74 / -46 / +40 / +70 / +90`，
而小字体 27px、对话字体 46px，`-74` 与 `-46` 必然重叠。现在：

1. 状态做成**胶囊** `DrawStatusChip()`：深色底（18,16,26,190）+ 上下 1px 亮边 + 文字，
   进度条画在胶囊内部底部（钓鱼时才画）；
2. 名字单独一行（-50），血条挂在名字下方；
3. 脚下合并成一行 `（模式）　背包 N`（+74）；
4. `[E] 对话` 挪到最下面（+94），只在靠近时出现；
5. 「跟着你」不挂胶囊（`ActionLabel` 返回空串）。


`GiveItem` / `TryMakeFish` / `LoadCompanionInventory` / `DropOreAt` 全部走这套校验，
升级时 `ModEntry.PurgeBrokenItems()` 把玩家背包和 AI 背包里已有的错误物品清掉
（`SaveLoaded` 触发，清完弹一条 HUD 说明）。

## 5. 配置与存档兼容

### 5.0 为什么自建设置菜单（2.0.7）

玩家反馈「标题界面没有扳手图标，F1 也没反应」。查日志定位到两件事：

1. **F1 从来不是 GMCM 的快捷键**（早期文档写错了）。GMCM 1.16 的入口是
   标题界面齿轮按钮 / 游戏内选项菜单底部。
2. 包里带的 GMCM 是 **1.11.2（SDV 1.5 时代）**。它在 1.6 上能加载、能注册 API、
   甚至能把我们的配置挂上去，但**不绘制图标**——SMAPI 日志里的
   `ALERT You can update 1 more mod: Generic Mod Config Menu 1.16.0` 就是线索。

于是把配置做进对话系统：`DialoguePage` 新增两个可选成员

```csharp
public Func<int,string> OptionText;   // 每行动态文本（实时显示当前值）
public Action<int,int>   OnAdjust;    // 左右键回调（-1/+1），设了它左右键就不移动光标
```

设置页用 `SettingRow { Text, Adjust, Choose }` 描述每一行，
`ShowSettingsPage(title, rows, cursor)` 负责渲染与重开（回车翻开关后
保持光标位置，否则会直接退出设置页）。改完的配置用 `_configDirty`
标记，在 `OnUpdateTicked` 里等对话关闭后统一 `Helper.WriteConfig`，
避免左右键连按触发大量写盘。

**踩过的边界**：`DialogueSystem.Cancel()` 会给 `OnChoose` 传 `-1`，
所有基于下标的 `OnChoose` 都必须先判 `i >= 0`（否则索引越界，
或误命中 `idx == -1` 的分支）。

**输入别只绑一套键**（2.0.9）：玩家反馈"设置页按左右键改不了"。代码逻辑没问题，
但只认 `SButton.Left/Right` 太窄（有人用 A/D、手柄 DPad、或键位被改过）。
现在四条路都通：方向键 / A·D / `回车=+1 步`（走 `Choose`）/ 鼠标点行
（`BlankMenu.receiveLeftClick` → `HandleClick`，用 Draw 时记录的选项区矩形命中）。
另加 `ai_cfg` 控制台命令当最后兜底，同时也是远程排查用的"打印当前配置"。

### 5.1 配置与存档

- `config.json` 键名是中文，**与 1.0.0 旧版完全一致**，旧存档无缝迁移。
  新增配置项（如 `行为_清理`）缺失时 SMAPI 自动取默认值，不需要迁移代码
- 改 `ModConfig` 时顺手改**五处**：字段、`Sanitize()`（如需要）、
  内置设置页（`HireRows/BehaviorRows/CombatRows/ScheduleRows/SaveRows`）、
  GMCM 注册、README/PROJECT 的参数表
- 新增可配置台词放 `文本_*`，占位符走 `ModEntry.Format()`（{name}/{price}/{wage}/{free}/{deaths}）；
  专属占位符（{groups}/{cap}/{now}/{sleep}/{left}）在调用处就地 `Replace`
- 存档数据键 `aihireling`（`SaveData`），字段名也别改
- `SaveData.背包物品` 用 `StoredItem`（ItemId + Stack）而不是 `Item`，
  新增字段天然向后兼容（旧存档反序列化后取默认空列表）
- GMCM 共 8 页：主页/雇佣与工资/死亡与涨价/工作行为/作息与移动/战斗/
  采集与挖矿/对话文本。加配置项时放进对应页的 Register 方法

## 6. 发布检查清单

1. [ ] 改功能，`dotnet build` 0 错误
2. [ ] 版本号三处同步（manifest/csproj/使用说明）
3. [ ] MD5 + UTF-16LE 字符串验证部署成功
4. [ ] `README.md` 版本行 + 使用说明更新记录
5. [ ] staging 目录剔除 pdb（**2.0.7 起不再附带 GMCM**），zip 后 CRC 校验
6. [ ] 进游戏实测（雇一次、切模式、看头顶标签、测试新功能）
7. [ ] 记忆日志追加（`.workbuddy/memory/YYYY-MM-DD`）

## 7. SDV 1.6 API 踩坑记录

### 7.1 已确认的 API 变动/怪癖

| 现象 | 真相 |
|------|------|
| 杂草没有 `TerrainFeatures.Weeds` 类 | 1.6 把杂草改成了普通物件：`StardewValley.Object.IsWeeds()` 方法判定 |
| `crop.dead` 编译报错 | 是 `NetBool`，要 `.Value` |
| `dirt.crop.Value = null` 报错 | `HoeDirt.crop` 是普通字段，直接 `= null` |
| 中文字体缺字形 | `↑↓▶` 等符号别用 |
| `Game1.exitActiveMenu()` 不触发 `exitFunction` | 菜单关闭不同步，必须每帧兜底 |
| 存档是个协程，不是一次性方法 | `SaveGame.Save()` 返回 `IEnumerator<int>`，`saveMode=7 / saveCompleteMode=8` 只是 gameMode 常量；白天存档要自己 `MoveNext()` |
| `Game1.IsSaving = true` 会冻结整个世界 | `Game1.Update` 里 `if (IsSaving) {... return;}`，`_draw` 里也只画菜单；所以推进协程不能只靠 `UpdateTicked` |
| `Game1.hooks` 是 `protected internal` | 模组里访问不到；但 `SaveGame.Save()` 内部会自己用，直接调它就行 |
| `Program.GetSavesFolder()` 是 public | 备份存档路径直接用它，别自己拼 AppData |

### 7.2 查 API 的正确姿势：编译探测

**不要反射**（被环境安全策略拦，也慢）。直接写候选代码让 C# 编译器报错：

```csharp
StardewValley.Object probe = null;
if (probe != null && probe.IsWeeds()) { }   // 编译过 → API 存在
```

属性还是方法、类型在哪个命名空间，编译器一次说清。

### 7.2.1 想看清游戏内部怎么实现的：ilspycmd

编译探测只能确认"存不存在"，看不出"什么时候会被调用"。要看真实的调用顺序/副作用，
直接反编译（本机已装，注意要用 `%USERPROFILE%\.dotnet\tools` 里的那份）：

```bash
ilspycmd -t StardewValley.SaveGame "E:/.../Stardew Valley.dll" > SaveGame.cs
ilspycmd -t StardewValley.Game1       "E:/.../Stardew Valley.dll" > Game1.cs
ilspycmd -t StardewModdingAPI.Framework.SGame "E:/.../StardewModdingAPI.dll" > SGame.cs
```

存档这块的关键结论（`SaveGame.Save` / `getSaveEnumerator` / `Game1._draw` / `SGame.Update`）
全是这么查出来的，比翻 GitHub 猜版本可靠。

带类型的精确签名/可见性用 `_tools/reflect`（MetadataLoadContext 小工具，不用跑游戏）：

```bash
cd _tools/reflect && dotnet run -- SaveGame Save     # 列出 SaveGame 里名字含 save 的成员
cd _tools/reflect && dotnet run -- Game1 IsSaving    # 顺手查可见性（pub/pri）
```另：游戏目录里有 `Stardew Valley.xml`（官方 XML 文档），
`grep 'M:StardewValley.SaveGame\.'` 能直接拿到公开方法的完整签名。

### 7.3 tools/list_types.py（备用）

自写的 .NET 元数据解析器，静态列出游戏 dll 指定命名空间的类型。
解析 `#~` 表的两个坑：**表行数按 mask 置位顺序连续存储**（不是固定下标）；
Module 表每行含 3×16 字节 GUID。一般用不到，编译探测更快。

### 7.4 音效名白名单

已在用的：`dirtyHit`(软打击/除草)、`axe`(砍/树枝/树桩)、`hammer`(镐/石头)、
`hoeHit`(翻地)、`wateringCan`(浇水)、`cropper`(收获/铲枯苗)、`coin`、`cast`、
`fishBite`。**不要用没验证过的音效名**（如 `scythe`），可能静默失效。

## 8. 版本历史（踩过的坑）

| 版本 | 内容 |
|------|------|
| 2.7.1 | 背包满自动找箱子：`JobType.存背包` + `FindChestForBag` + `DoStoreBag`，`Think` 先箱子后玩家；配置 `背包满了自动找箱子`；箱子满 30s 冷却、塞不下留背包 |
| 2.7.0 | 绘制锚点对齐原版：`LiftY = 32` 上提精灵/影子/名字/胶囊/脚下标签/交互提示/钓鱼手部。原版影子在 `Position+(32,24)`、脚底 `Position+48`，AI 原来影子 `+58`、脚底 `+64`，低半格导致草丛埋人、门口"站进台阶"、树冠判定偏移 |
| 2.6.9 | ① 树虚化/遮挡判定遍历方向错误（`cy-6..cy+1` 应为 `cy-1..cy+7`，树根在人下方，树冠画在上方）；② `FollowPlayer` 加 2 秒零位移的 `HopToward` 兜底 + `[跟随]` 日志 |
| 2.6.8 | ① 树虚化阈值 5→4 段（4 段用 192x224）；② `HasBuildingAt()` 拦建筑占位格（`CanStandAt` + `TryMove`，已在格内时例外放行） |
| 2.6.7 | 树后虚化：新增 `FadeCoveringTrees()`，在 UpdateTicked 里用 AI 的 48x48 脚部盒匹配原版树冠矩形，把 `tree.alpha`/`fruitTree.alpha` 压到 0.4（与玩家一致） |
| 2.6.6 | 上半身穿模：新增 `CoveredByTallThing()`，树冠用 192x192 估算矩形求交、`Bush` 用 boundingBox，命中则不补画上半身 |
| 2.6.5 | 原版层序接好后又出现"草太密就只剩皮肤"：新增 `DrawUpperBody()`，在 RenderedWorld 补画精灵上半截（`frame.Height / 2`）压在草上，腿仍被草盖；`TintOf()` 抽出复用。只遮挡开启时补画 |
| 2.6.4 | 2.6.3 的补画方案翻车（草叠两层把人埋了）。改用 Harmony patch `GameLocation.drawFarmers` 的 Postfix，把身体画在"玩家之后、objects/地形前景之前"，遮挡与原版一致且不重复绘制；渲染拆 `DrawBody` / `DrawOverlay`（UI 仍在 RenderedWorld，不会被草盖）；补丁失败自动回退；csproj 引 `0Harmony`（`Private=false`） |
| 2.6.3 | 身体遮挡：`RenderedWorld` 在最后画导致 AI 永远压在地形之上。新增 `CompanionRenderer.DrawOverlap()`，按原版层序（objects → terrainFeatures/`drawAboveFrontLayer`）把与身体矩形相交的格子补画一次；`CellHits` 用 96x112 的溢出范围判定；跳过 `Flooring`。配置 `身体被作物遮挡`（默认开）。注意 `SpriteSortMode.Deferred` 下 layerDepth 无效，遮挡只能靠调用顺序 |
| 2.6.2 | 控制台美化：新增 `ConsoleArt.cs`（ANSI 色 + BMP 图标 + `Banner`）与 `CLog` 封装；`Init()` 用 `SetConsoleMode(0x0004)` 打开 Windows 控制台 ANSI 解析（SMAPI 自己不设，否则显示成 `←[...m`）；65 处日志调用语义化替换（Info/Ok/Step/Tip/Note/Warn/Error/Act/Trace）；`[标签]` 自动高亮；图标避开 emoji（cmd 字体无字形）；设置加「控制台显示」页 + GMCM 分组 |
| 2.6.1 | 全功能巡检：设置页 21 行拆三页（对话框会顶出屏幕）、`ShowSettingsMenu` 表驱动；`OpenCompanionInventory` 改 `Chest.addItem`（>36 组不再丢失）+ 死亡拒绝；工具回收 leftover 丢脚边；自动模式砍树限定 `Farm`；`FinishFishing` 空军、工资结算、重生问候全部改 HUD（不再弹阻塞对话框）；领取物品支持单项；`JobType.砍树` 宽限 9s；`PostponeBagAlert` 2 分钟→2 小时；背包满询问冷却 8s→60s；`MiningBlockReason` 提出循环；清 4 个 SMAPI 告警；注释归位 |
| 2.6.0 | 新增「砍树」工作模式：`AiMode.砍树`（插在 `农场清理` 之后，枚举值不带持久化，改顺序安全）；`Think()` 砍树分支改成 `cfg.行为_砍树 && (Mode == 砍树 || (自动 && inRange))`；打怪分支把 `砍树` 也算进"会自卫"的列表。`ShowModeMenu()` 重写为表驱动 `(AiMode Mode, string Label, string Hint)[] modes` + `ModeText(mode)`，**以后加模式只动数组，不用再手数 case 0/1/2**（老写法每加一个模式都要改后面所有下标）。新增 `文本_砍树模式`。教训：菜单项一多，索引式 switch 就是 bug 温床 |
| 2.5.3 | 提示刷屏：`WarnBlocked` 原来只有 45 秒节流，缺鱼竿就一直喊。新增 `提示每天最多次数`（2）/`提示静默天数`（3）+ `SaveData.提示记录`（`HintRecord`: Key/天/次数/静默到），`HintAllowed()` 跨天归零、说满即静默；会话间隔 60s；被静默时不写 Info 日志；设置页与 GMCM 都加了两项；顺手删掉 `DoFishing` 里 `return` 之后的死代码（CS0162） |
| 2.5.2 | 启动日志的可读性：原来「参数 对话 →「设置」，或控制台 ai_cfg（不带参数就看用法）」一堆括号和箭头，玩家反馈看不懂。改成三行「找他说话 / 随时存档 / 改参数」，改参数下面再列 ① 游戏内按 E →「设置」 ② SMAPI 窗口输入 ai_cfg 回车（会把参数全列出来）。注意 SMAPI 多行日志只有首行带前缀，续行缩进自己写 |
| 2.5.1 | 遣散后不再站在原地：`FollowPlayer()` 把 `!IsHired` 分支提到 `Mode == 待命` 早退之前，在农场里 `GoHome()` 走回农舍门口；`GoHome()` 新增 4 秒零位移兜底传送；`OnWarped()` 玩家回农场时把被落在别图的已遣散 AI 带回门口；`Dismiss()` 加 HUD 提示；`ActionLabel()` 区分「回门口中 / 在门口待命」 |
| 2.5.0 | ① 挖矿同钓鱼收费：`FindPickaxe()` / `MiningEnergyCost()`（`2 - 挖矿等级*0.1`）/ `MiningBlockReason()`；`DoMining` 每挥一次扣体力，体力不够收手；清理石头（390）也走同一套；`WarnFishingBlocked` 改名 `WarnBlocked`（没鱼竿/没镐子/没体力统一提示）；② 对话加「打开他的背包」：临时 `Chest` + `Chest.ShowMenu()`，`_companionBagOpen` 冻结 AI，**轮询 `activeClickableMenu` 判关闭**（Chest 每次抓取都重建菜单，exitFunction 会丢），关掉回写并存档；工具不进 AI 背包（还给玩家），`InventoryGroups` 与领取/塞箱都排除工具 |
| 2.4.0 | 钓鱼按原版收费（玩家："没有任何花费，太作弊了"）：① `FindFishingRod()` 要有竿（AI 背包 → 玩家背包）；② `TryConsumeBait()` 每竿 1 个，先用 AI 的再用玩家的，没饵咬钩 ×1.9；③ 新增 `Companion.Energy` / `MaxEnergy` / `EnergyRatio`，每竿扣 `8 - 钓鱼等级*0.1`，`SaveData.体力` 存档、`OnDayStarted` 回满、`RegenStamina()` 按模式慢回；④ `BeginCast()` 变成"结账点"，付不起就 `WarnFishingBlocked()` + 20 秒歇着；⑤ `Think()` 钓鱼分支先判 `FishingBlockReason()`，没竿/没体力直接跟随；⑥ 咬钩时间有饵 ×0.85、无饵 ×1.9、再抖 ±20%；渲染加体力条与「（无饵）」标签 |
| 2.3.2 | 控制台可读性：manifest Description 压到 56 字；`Entry` 启动日志改多行区块；`ai_cfg` 输出拆三块并按 工资/战斗/移动/行为/背包/存档 分组（新增 `OnOff()` 显示开/关）。**SMAPI 的多行日志只有第一行带 `[INFO 模组名]` 前缀，后续行原样输出——所以续行自己带 4 空格缩进** |
| 2.3.1 | 头顶 HUD 重排：新增 `DrawStatusChip()`（深色底框 + 文字 + 可选内置进度条）；布局改为 胶囊(-94) / 名字(-50) / 脚下一行 模式+背包(+74) / 交互提示(+94)；`ActionLabel` 在"跟随中"返回空串（不再无条件挂框）。教训：HUD 每加一项都要算一下垂直空间——小字体 27px、对话字体 46px，-74/-46 这种间距必然重叠 |
| 2.3.0 | 新增「只在睡觉时间能存档」（玩家要求）：`ModConfig.存档需到睡觉时间` + `文本_存档未到点`；`ModEntry.SaveTimeAllowed(out reason)` 比较 `Game1.timeOfDay` 与 `回家睡觉时间`，没到点用 `MinutesOfDay/DurationStr` 算出「还差 4 小时 30 分钟」，三个入口（F5 / 对话 / `ai_cfg save`）都拦；内置存档页 + GMCM 都加开关 |
| 2.2.4 | 2.2.3 只推一格仍落在沙地：岸边 1~2 格的贴图都盖着沙滩。改为 ① `DeepenWaterTile` 连续推最多 3 格、直到 `IsInteriorWater`（四邻全水）；② 渲染加 `FindWaterSpriteTile`，浮漂只画在纯水面格（3 格内就近找）；③ 钓鱼下竿距离 3→4.5 格。**记住：`isWaterTile` 真 ≠ 贴图是水** |
| 2.2.3 | 修「鱼钩没在水里」：水面贴图的岸边格上半截是沙滩，浮漂画在 `FishTile*64+(32,32)`（格中心）会落在沙边上。① `FindWaterTile` 返回值经 `DeepenWaterTile(best, here)`：沿 AI→水的主轴再走一格，要求仍是水、未被拉黑、距 AI ≤4 格；② `CompanionRenderer.PushIntoWater()` 按四邻水/岸给浮漂 12px 偏移。`DrawFishing` 里先偏移再算角度，保证竿/线/涟漪一致 |
| 2.2.2 | 「任何位置都不卡」：日志显示 AI 在 (26,88) 来回抖，`Tile` 在相邻两格间跳（`同格 0.0s`），所以按格子判定的计时器同样失效。改为在 `ApproachTimeout()` 内部做**位置挪移**兜底：2.5 秒零进展 → `HopToward(target, 2)` 找一个"`CanStandAt` 且更靠近目标"的格子直接挪过去，最多 3 次；改善后再正常走路。砍树/挖矿/清理/采集/钓鱼共用同一条兜底，彻底告别"永远卡住"。钓鱼挪满 3 次才 `GiveUpAndFollow()`。教训：**位置微抖会让"是否移动"类判据全部失效，卡死兜底要直接改变位置，而不是继续加超时** |
| 2.2.1 | 修「卡 3 秒换跟随」实际卡 11 秒：`_fishWalkStuck` 用 `WasMoving`（像素位移 >2px）累加，墙角抖动会让它反复清零；改成 `_fishLastTile + _fishTileSince` 按**格子**判定，3 秒不换格即 `GiveUpAndFollow()`。同时远距离接近超时收紧为 `4s + 0.4s/格`（≤12s），诊断日志加「同格 N.Ns」字段 |
| 2.2.0 | 新增「背包满了自动找玩家」（玩家要求"背包满了给玩家还是放箱子里"）：`ModConfig.背包容量组数`（默认 12，0=不限）、`背包满了自动找玩家`、`文本_背包满`（{groups}/{cap} 占位符）；`Companion.IsBagFull` + 新 `JobType.送背包` + `DoDeliverBag()`（>1.6 格就提速走过去，超出瞬移距离直接传送）；到面前调 `ModEntry.AlertBagFull()` 弹三选一：`HandOverItems()` / 新增 `StoreBagToChest()`（`FindNearestChest` 取 12 格内最近箱子，塞不下掉脚边）/ `PostponeBagAlert(2h)`；`_bagTellCooldown` 防刷屏（说完 8 秒内不重复）；待命/跟随模式下只在玩家 3 格内才开口 |
| 2.1.5 | 2.1.4 的日志显示已经能钓到鱼（137 小嘴鲈鱼 / 145 太阳鱼），但存在"我(26,99) 水面(26,101) 差 2 格 位移=无"每 7 秒一次的空转。① 下竿距离 1.5 → **3 格**（原版鱼竿 5 格），`BeginCast()` 抽出；想再靠近最多多试 1.5 秒；② 新增 `GiveUpAndFollow()`：**连续 3 秒零位移就直接把 `Mode` 切到 `跟随`** + HUD 提示（玩家提案"卡 3 秒换跟随"）；③ `FailFishing` 仍只用于"在动但靠不近"的超时（拉黑 3x3 + 8~28 秒退避）。教训：`switch` 里同样的 `case X:` 文本在别的方法里也出现，替换代码时不能用 `s.index("case ...")` 定位，必须带上下文注释行 |
| 2.1.4 | 钓鱼仍在原地（2.1.3 日志：`我在 (28,82)，目标水面 (14,96)，距离 20 格` → 6 秒判超时）。① `ApproachTimeout` 加 `limitMs`，钓鱼按 `6s + 0.5s/格`（上限 20s）；② `Think` 在 `Phase == 走去水边` 时不再死锁目标，允许接力到"近 2 格以上"的水面；③ `TryMove` 增加 `CanMoveTo` 双盒判定：大盒（48x48）撞墙时退回脚下小盒（28x26），解决"贴着房子永远迈不出第一步"；④ 走路 1.2 秒无位移 → `FindNearestStandable` 就地挪一格脱困，2.5 秒仍不动才 `FailFishing`；⑤ `UpdateStuck` 通用兜底 5s→2.5s；新增 1.5s/次走路诊断日志 |
| 2.1.3 | 修「头顶写着去水边、人却一步不动」（2.1.2 截图反馈）。真根因不在钓鱼而在**寻路**：`BuildPath` 的 BFS 节点上限 240，BFS 一圈圈扩散时 240 个节点只覆盖半径 7~8 格，农场从农舍到池塘（10+ 格）中间还隔着房子 → 直线被挡、BFS 也找不到路，`MoveToward` 里 `_path.Count == 0` 直接 return，AI 就一动不动；`ApproachTimeout` 6 秒后拉黑一格水、下一格还是同一段岸边，于是「去水边」永久挂着。修法：① BFS 预算 240→800 节点、±16→±22 格；② `_path` 为空时沿垂直方向贴障碍侧滑（不再原地不动）；③ 钓鱼废弃"预挑岸边站位"（`FindStandTileNear`/`IsGoodShore` 删除），改成朝水格走、贴到八邻（≤1.5 格）就抛竿；④ 新增 `FailFishing`：2.5 秒挪不动或 6 秒靠不近就拉黑水格 3x3、歇 8~28 秒（连击递增），休息期不接钓鱼活；⑤ 启动日志版本号改读 `ModManifest.Version`（之前写死 2.1.1，日志版本对不上） |
| 2.1.2 | 修「钓鱼一直显示钓鱼中、也没有动画」（玩家反馈）：`FindStandTileNear` 固定顺序取岸位、且不判 `CanStandAt`，海滩潮池会选中礁石围住的格子；`FindWaterTile` 又完全不看 `Avoid` 黑名单，于是「6 秒靠不近 → 拉黑 → 立刻选回」死循环，`FishStartedAt` 永远停在 -1（连鱼线都不画），头顶一直「钓鱼中」。现在：岸位必须 `CanStandAt` 且挑离 AI 最近的一侧；水面搜索过滤黑名单（每圈最多验 24 格）；`Think` 在 `Phase != 无` 时锁死 `Job.Tile = FishTile`，不再每 350ms 重选；钓鱼拆成 走去水边/抛竿/等待/咬钩/收竿 五阶段（420/配置/450/350ms），渲染器新增鱼竿摆动、浮漂抛物线、涟漪、水花、进度条与分阶段标签；各阶段重置 `_stuckTimer`，连续多竿不再被静止检测打断 |
| 2.1.1 | 修「钓鱼钓出错误物品（754）」（玩家反馈）：旧版「可钓鱼类列表」混进了 753/754 等不存在的 ID，`ItemRegistry.Create` 对不存在的 ID 不抛异常而是返回 Error Item，塞进玩家背包后删不掉。改为三级来源：配置点名的 ID（先过 `IsRealItemId`）→ 原版 `loc.getFish(500f, null, 3, Game1.player, 0.0, bobberTile, null)`（地图/季节/天气/水深/钓鱼等级全对，鱼塘格跳过避免捞走玩家的鱼）→ `Data/Objects` 中 `Category == FishCategory` 的鱼表；捞到 junkCategory 杂物再抛一竿。升级后 `PurgeBrokenItems()` 清掉玩家背包/AI 背包里的错误物品（只认纯数字 ID + 表里查不到），新增 `ai_cfg fishpool` |
| 2.1.0 | 新增「随时存档」（玩家要求"原生保存功能"）：`NativeSave.cs` 驱动原版 `SaveGame.Save()` 协程，F5 / 对话「立即存档」/ `ai_cfg save` 三个入口；置 `Game1.IsSaving` 冻结世界并显示存档画面；协程推进点同时挂 `UpdateTicked` + `Rendered`（存档时 `Game1.Update` 提前 return，只挂一处会卡死），60 秒超时兜底；单人限定（联机要 ready 同步）、节日/剧情拒绝（`cleanupBeforeSave` 会删 Tent）；存档前每天备份一次当天开局存档 |
| 2.0.20 | 修「钓鱼只有甩杆没有渔获」：`UpdateStuck` 原来一律 2.5 秒没位移就放弃任务，而钓鱼要静立 9 秒（`钓鱼等待毫秒`）→ 每次都被打断重置。改成按任务类型给宽限（钓鱼 = 等待时长+6s，其它干活 5s），钓鱼放弃时不拉黑水面；钓到鱼/生成失败都会写日志 |
| 2.0.19 | 彻底移除挥击特效（连同 2.0.18 的细痕）——玩家反馈任何叠在角色身上的白色特效都难看；动作状态只靠头顶标签表达 |
| 2.0.18 | 挥击特效重画：原来是一块 46x10 实心白矩形（`_pixel` 缩放）压在胸口，像白布条；改成细长半透明暖白划痕 + 朝朝向方向偏移旋转 |
| 2.0.17 | 移除手持工具绘制（`drawInMenu` 画出的图标叠在角色身上像白条，观感差）；保留 `FindTool`（砍树仍走原版 performToolAction） |
| 2.0.16 | 修「取不到工具」告警：`ToolMatches` 改成先比 `GetType().Name`（Axe/Pickaxe/Scythe… 都是独立类，比 ItemId 可靠），再比 ItemId、最后比显示名；`GetTool` 只缓存成功结果（玩家以后把工具从箱子里拿出来还能扫到）；找不到时日志降为 Trace |
| 2.0.15 | 去掉启动日志里的「未安装 GMCM」提示（GMCM 已改为纯可选，提示属于噪音） |
| 2.0.14 | 自动模式优先级调整：清理地面提到砍树之前（农活 → 采集 → 清理 → 砍树 → 钓鱼） |
| 2.0.13 | 修黑名单打错目标导致的死循环（`ApproachTimeout(stand)` 只黑 stand，搜索按 `Job.Tile` 过滤 → 每 6 秒重复同一目标；现在两个都黑）；`FindStandSpot` 改挑离 AI 最近的一侧；工具图标改用 `Tool.drawInMenu`（`IndexOfMenuItemView=215` 但工具表只有 336x384，手算 `215*16=3440` 画到贴图外，所以一直看不见） |
| 2.0.12 | 砍倒走原版：最后一刀把 health 压到 1 再调 `tree.performToolAction(axe,0,tile)`，借原版播放倒下动画并按原版规则掉落（失败回退手动 set stump）；工具改为"干活期间一直手持"（原来只在 AttackAnimPhase 的 220ms 内绘制，肉眼几乎看不到）；加一次性工具诊断日志（工具对象/贴图/索引） |
| 2.0.11 | 修「在砍树但树不倒」：站位判定 1.2~1.4 → 1.9 格（斜角可砍）；新增 `ApproachTimeout`（6 秒靠不近就拉黑，补 `UpdateStuck` 只管"没动"的空白）；砍树校验目标树仍在地图上；同树 15 刀未倒强制放倒 + 血量 Trace 日志 |
| 2.0.10 | 修「点一下闪一下」：`Choose` 不再先 `Next()`(Close) 再回调，改为一律先回调、只在回调没重开对话（`_openEpoch` 未变）时才翻页；设置页用 `OnActivate` 原地改值不重建；`HandleClick` 加对话框内兜底命中 |
| 2.0.9 | 设置页四路输入（方向键/A·D/回车+1步/鼠标点行）+ `ai_cfg` 控制台命令；数值用【】标出 |
| 2.0.8 | 寻路重做（直线+滑动 → BFS 绕路 → 拉黑 → 脱困）；手持游戏自带工具（优先玩家那把） |
| 2.0.7 | 内置设置菜单（对话→设置，4 页 20+ 项，不依赖 GMCM）；不再附带过期 GMCM 1.11.2；更正「F1 打开配置」的错误说明 |
| 2.0.6 | 修复多段动作进度被 350ms 重新决策清零（「显示清理中但不执行」/挖矿同病）；AI 背包 + 对话免费领取 + 存档持久化 + 死亡掉地 |
| 2.0.5 | 首次雇佣免费（`Data.曾经受雇` 区分首雇/重雇；雇佣费只用于遣散/死亡后重雇） |
| 2.0.4 | 新增农场清理模式（杂草/枯苗/树枝/石头/树桩）；自动模式兜底含清理 |
| 2.0.3 | 重画十二帧像素形象；头顶实时行动标签；`IsWalking` 改真实位移 |
| 2.0.2 | 对话方向键直选（删了坏的两步式 `_showingOptions` 状态机）；中文乱码修复；选项多时自动增高 |
| 2.0.1 | AI 跨地图守卫（不追进屋/穿墙）；未雇佣守门；`SyncMenuState` 兜底（WASD 卡死根因：游戏侧关菜单不同步 + 右键无法关闭 + 被外来菜单顶替后永不摘除） |
| 2.0.0 | 首个分发包 |

**经典 bug 模式复盘**：这个项目 80% 的 bug 都是「状态没同步」——
AI 位置与地图不同步、对话与菜单挂载不同步。以后加功能先问自己：
**这个状态有几个真相来源？怎么保证它们一致？**

第二类坑是「节流/间隔不匹配」：决策 350ms 一次，而动作（打击、冷却）需要
700ms 才完成一个周期——任何写在 `Job` 上的累积状态都会被决策重置。
凡是跨帧累积的东西，都要在重新决策时显式继承。

第三类坑是「放弃后立刻回头」：卡住 → 放弃任务 → 下一次决策又选中同一个
最近目标 → 永远卡在原地。凡是"放弃某个目标"的路径，都必须留下痕迹
（这里是 `Avoid()` 黑名单），否则等于没放弃。

## 9. 给 AI 助手（下次继续这个项目时）

- 用户中文交流，反馈问题常直接发游戏截图，重效率别绕弯
- 每次构建后必须做 §2.2 的三板斧验证，别只凭"编译通过"就报完成
- **玩家说"某个功能没反应"，先看 `%APPDATA%\StardewValley\ErrorLogs\SMAPI-latest.txt`**：
  游戏版本、SMAPI 版本、依赖模组版本、有没有 ALERT 升级提示都在里面，比猜快得多
- 别把"某个第三方模组的界面"当成唯一入口——关键功能自己做一份，玩家永不被卡
- 打包分发前问自己：pdb 剔了吗？版本号三处同步了吗？CRC 验了吗？
- 改 UI 文案前检查中文字体缺字形问题
- 贴图改动走 `tools/make_sprite.py`，别手改 PNG

### 卡死与定位的三条铁律（2.1.3~2.2.2 的血的教训）

1. **别用"有没有动 / 有没有换格"判断卡死**。AI 贴着墙时碰撞盒一直重叠，每帧只能抖
   几像素，`WasMoving`（>2px）为真、`Tile` 在两个格子间来回跳——两种判据都会把计时器
   反复清零（2.2.0 的"3 秒"实际卡了 11 秒、2.2.1 按格子判定同样失效）。
   **正确的兜底是直接改变位置**：`HopToward()` 找个「能站且更靠近目标」的格子挪过去。
2. **`isWaterTile` 为真 ≠ 贴图是水**。岸边 1~2 格在数据里算水面，但贴图画着沙滩，
   画/落点在那儿就像"扔在岸上"。视觉相关的判定要用「四邻全水」这种更硬的判据。
3. **判断跑的哪个版本看 SMAPI 日志的 `assembly version`**，别信模组自己打的日志
   （2.1.1 曾在代码里写死版本号，日志对不上，白排查一轮）。

### 物品 ID 的两条铁律（2.1.1 的血的教训）

1. **`ItemRegistry.Create` 对不存在的 ID 不抛异常**，而是返回一个 Error Item
   （名字「错误物品（ID）」、禁止符号图标），塞进背包后捡不起也卖不掉。
   所以任何"按 ID 造物品"的地方，先 `Companion.IsRealItemId(id)` 再 Create，
   Create 之后再用 `IsBrokenItem(item)` 兜一层。
2. **别手写物品 ID 清单**。宁可留空然后从 `Game1.objectData` 里按
   `Category`/`Type` 现算（钓鱼就是这么改的），也不要凭记忆编——编错不会报错，
   只会在玩家背包里长出一堆删不掉的错误物品。

### 存档相关的三条铁律（血的教训）

1. **不要自己序列化游戏状态**。要存盘就推原版 `SaveGame.Save()`；白天推它需要
   `Game1.IsSaving = true` 冻结世界，否则后台线程在序列化的同时世界还在变 → 崩/坏档。
2. **协程推进点至少挂两个**（`UpdateTicked` + `Rendered`）。`IsSaving` 期间 `Game1.Update` 直接 return，
   单点驱动一旦失效，玩家就永久卡在存档画面。
3. **动存档前先备份**。`Program.GetSavesFolder()` + `SaveGame.FilterFileName(...)+"_"+uniqueIDForThisGame`
   算出现有存档目录，原样 `File.Copy` 一份即可；别去读/改 XML 内容。
