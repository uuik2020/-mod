using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace AIHireling;

/// <summary>
/// AI 雇工的屏幕绘制：影子、精灵、攻击特效、钓鱼线、血条、名字与交互提示。
/// 在 ModEntry 的 RenderedWorld 事件中调用。
/// </summary>
public static class CompanionRenderer
{
    private static Texture2D _sprite;
    private static Texture2D _pixel;

    public static void Init(Texture2D sprite)
    {
        _sprite = sprite;
        if (_pixel == null || _pixel.IsDisposed)
        {
            _pixel = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
            _pixel.SetData(new[] { Color.White });
        }
    }

    /// <summary>工具物品缓存（斧/镐/镰/锄/喷壶），用游戏自带的工具贴图。</summary>
    private static readonly Dictionary<string, Tool> _tools = new Dictionary<string, Tool>();

    private static Tool GetTool(string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        // 优先用玩家背包里那把同款工具：贴图、升级等级都和玩家完全一致
        try
        {
            Farmer p = Game1.player;
            if (p?.Items != null)
            {
                foreach (Item item in p.Items)
                {
                    if (item is Tool t && ToolMatches(t, id))
                        return t;
                }
            }
        }
        catch { }

        if (_tools.TryGetValue(id, out Tool cached) && cached != null)
            return cached;

        // 玩家身上没有（工具可能放在箱子里）→ 造一把同款。
        // 工具 ID 在各版本写法不一致（喷壶 "Watering Can"、镰刀可能是 GoldenScythe），
        // 逐个试，全都失败就当作没有，不影响其它功能。
        string[] candidates = id switch
        {
            "WateringCan" => new[] { "(T)Watering Can", "(T)WateringCan" },
            "Scythe" => new[] { "(T)Scythe", "(T)GoldenScythe" },
            _ => new[] { "(T)" + id },
        };

        Tool tool = null;
        foreach (string cid in candidates)
        {
            try
            {
                tool = ItemRegistry.Create(cid) as Tool;
            }
            catch
            {
                tool = null;
            }
            if (tool != null)
                break;
        }

        // 只缓存成功的结果：万一玩家后来把工具从箱子里拿出来了，下次还能扫到
        if (tool != null)
            _tools[id] = tool;
        return tool;
    }

    private static bool ToolMatches(Tool t, string id)
    {
        // 1) 类型名：Axe / Pickaxe / Hoe / WateringCan / Scythe … 是各自的类，
        //    这条最可靠（1.6 里镰刀的 ItemId 不一定以 "Scythe" 结尾）。
        string typeName = t.GetType().Name;
        if (typeName.Equals(id, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2) 物品 ID（兼容 "Scythe" / "GoldenScythe" / "Watering Can" 等写法）
        string tid = t.ItemId ?? "";
        if (tid.EndsWith(id, StringComparison.OrdinalIgnoreCase))
            return true;
        if (id == "WateringCan" && tid.IndexOf("Watering", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // 3) 最后按中英文显示名兜一下（"镰刀"/"Scythe"）
        string name = t.DisplayName ?? "";
        return name.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0
               || (id == "Scythe" && name.Contains("镰刀"));
    }

    /// <summary>
    /// sprite 图为 12 帧 16x24 横排（4 方向 x 待机/走1/走2）：
    /// 0-2 朝下、3-5 朝左、6-8 朝右、9-11 朝上。
    /// facing：0=上 1=右 2=下 3=左（与 Companion.MoveToward 一致）。
    /// walkPhase：0=待机 1=走1 2=走2。
    /// </summary>
    private static Rectangle FrameFor(int facing, int walkPhase)
    {
        int group = facing switch
        {
            0 => 9,   // 上
            1 => 6,   // 右
            2 => 0,   // 下
            3 => 3,   // 左
            _ => 0,
        };
        int idx = group + (walkPhase < 0 ? 0 : walkPhase > 2 ? 2 : walkPhase);
        return new Rectangle(idx * 16, 0, 16, 24);
    }

    /// <summary>
    /// 头上是不是压着树 / 果树 / 灌木这类"高的东西"。
    ///
    /// 补画上半身是为了让他从草丛里露出来；但如果背后是树，补画就会让脑袋穿过树冠、树干，
    /// 看着像贴图穿模（玩家截图）。所以这种情况老老实实让原版把他挡住。
    /// </summary>
    private static bool CoveredByTallThing(Companion c)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return false;

        Rectangle body = new Rectangle((int)c.Position.X, (int)c.Position.Y - 32, 64, 96);
        int cx = (int)(c.Position.X / 64f);
        int cy = (int)(c.Position.Y / 64f);

        // ① 树 / 果树：树冠贴图是 48x48（放大 4 倍 = 192x192），画在根部格子上方
        //    （矩形约 [ty*64-320, ty*64-32]），所以**树根在人下方 0~6 格**才会盖到人头上，
        //    这里要往下查，不是往上查。
        for (int ty = cy - 1; ty <= cy + 7; ty++)
        {
            for (int tx = cx - 2; tx <= cx + 2; tx++)
            {
                if (tx < 0 || ty < 0)
                    continue;
                if (!loc.terrainFeatures.TryGetValue(new Vector2(tx, ty), out TerrainFeature tf) || tf == null)
                    continue;
                if (tf is not Tree && tf is not FruitTree)
                    continue;

                Rectangle canopy = new Rectangle(tx * 64 - 64, ty * 64 - 128, 192, 192);
                if (canopy.Intersects(body))
                    return true;
            }
        }

        // ② 灌木（1.6 的 largeTerrainFeatures）
        try
        {
            foreach (LargeTerrainFeature big in loc.largeTerrainFeatures)
            {
                if (big is Bush && big.getBoundingBox().Intersects(body))
                    return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// AI 站到树后面时，让那棵树像"玩家在后面"一样虚化 —— 原版只认 Game1.player。
    ///
    /// 原版逻辑在 <c>Tree.updateTick</c> / <c>FruitTree.updateTick</c> 里，每帧算一次：
    /// <code>
    ///   alpha = min(1, alpha + 0.05);                    // 慢慢恢复不透明
    ///   if (玩家碰撞盒 与 树冠矩形 相交) alpha -= 0.09;   // 最低 0.4
    /// </code>
    /// 树的 update 在 <c>Game1.Update</c> 里跑，而 SMAPI 的 UpdateTicked 在 update **之后**触发，
    /// 所以这里把 alpha 压到 0.4 会被本帧的绘制直接用上；下一帧原版 +0.05 想恢复，
    /// 我们再压回去，视觉上就是稳定的半透明。
    ///
    /// 树冠矩形和原版完全一致（果树的阈值是 4 段、普通树是 5 段），
    /// 只有"长成了、没在倒下、也不是树桩"的树才虚化。
    /// </summary>
    public static void FadeCoveringTrees(Companion c)
    {
        if (c == null || !c.Active || c.Dead)
            return;
        if (ModEntry.Config?.身体被作物遮挡 != true)
            return;

        GameLocation loc = Game1.currentLocation;
        if (loc == null || (c.Location != null && c.Location != loc))
            return;

        // 用他脚下的碰撞盒（和移动/站位用的是同一个），几何上和原版玩家一致
        Rectangle feet = new Rectangle((int)c.Position.X + 8, (int)c.Position.Y + 16, 48, 48);

        int cx = (int)(c.Position.X / 64f);
        int cy = (int)(c.Position.Y / 64f);

        // 树冠画在根部格子上方（矩形 [ty*64-320, ty*64-32]），
        // 所以能盖到人的树，树根都在他下方 0~6 格 —— 往下查，别往上查。
        for (int ty = cy - 1; ty <= cy + 7; ty++)
        {
            for (int tx = cx - 2; tx <= cx + 2; tx++)
            {
                if (tx < 0 || ty < 0)
                    continue;
                if (!loc.terrainFeatures.TryGetValue(new Vector2(tx, ty), out TerrainFeature tf) || tf == null)
                    continue;

                // 果树：长成（>=4 段）后树冠比普通树矮一格
                if (tf is FruitTree fruit)
                {
                    if (fruit.growthStage.Value < 4 || fruit.falling.Value || fruit.stump.Value)
                        continue;
                    if (feet.Intersects(new Rectangle(64 * (tx - 1), 64 * (ty - 4), 192, 224)))
                        fruit.alpha = Math.Min(fruit.alpha, 0.4f);
                    continue;
                }

                // 普通树：>=4 段就有像样的树冠了（原版只对 >=5 段虚化，
                // 但 4 段那种"半大树"一样能把他整个人挡住，所以这里放宽一段）
                if (tf is Tree tree)
                {
                    int stage = tree.growthStage.Value;
                    if (stage < 4 || tree.falling.Value || tree.stump.Value)
                        continue;

                    Rectangle canopy = stage >= 5
                        ? new Rectangle(64 * (tx - 1), 64 * (ty - 5), 192, 288)
                        : new Rectangle(64 * (tx - 1), 64 * (ty - 4), 192, 224);
                    if (feet.Intersects(canopy))
                        tree.alpha = Math.Min(tree.alpha, 0.4f);
                }
            }
        }
    }

    /// <summary>受击时闪红用的着色。</summary>
    private static Color TintOf(Companion c)
    {
        return (c.Clock < c.InvulnerableUntil && (int)(c.Clock / 90.0) % 2 == 0)
            ? new Color(255, 120, 120)
            : Color.White;
    }

    /// <summary>
    /// 把精灵的**上半截**再画一次，压在草叶上面。
    ///
    /// 身体已经交给原版层序（会被草、作物、树盖住，和玩家完全一样），
    /// 但如果他站在草丛正中间——上下左右都是草——就会被盖得只剩一点皮肤，
    /// 玩家根本看不见自己雇的人（截图里就是这样，玩家站在草丛边缘所以露着上半身）。
    /// 这里补画头 / 肩 / 胸，效果就接近"人站在草里露出上半身"：
    /// 腿脚埋在草里，上半身从草叶间冒出来。
    /// 切口落在胸口，正好是草叶最密的地方，看不出来。
    /// </summary>
    private static void DrawUpperBody(SpriteBatch b, Companion c)
    {
        if (!Ready(c, out Vector2 local) || c.Dead)
            return;

        // 头顶压着树/灌木的时候不能再补画，否则上半身会"穿"到树冠、树干上面
        if (CoveredByTallThing(c))
            return;

        int walkPhase = (!c.Sleeping && c.IsWalking)
            ? 1 + (int)(c.WalkAnimTime / 140.0) % 2
            : 0;
        Rectangle frame = FrameFor(c.Facing, walkPhase);

        // 只取上半截（16x24 帧里的上 12 像素 = 头 + 肩 + 胸）
        Rectangle upper = new Rectangle(frame.X, frame.Y, frame.Width, frame.Height / 2);

        b.Draw(_sprite, local + new Vector2(0f, -32f - LiftY + BobOf(c)), upper, TintOf(c),
            0f, Vector2.Zero, 4f, SpriteEffects.None, 0.002f);
    }

    /// <summary>
    /// AI 的绘制比原版角色**低了 32px（半格）**，这里统一往上提。
    ///
    /// 依据（反编译源码）：
    /// <code>
    ///   // Farmer.cs:9199  玩家影子
    ///   b.Draw(Game1.shadowTexture, GlobalToLocal(GetShadowOffset() + Position + new Vector2(32f, 24f)), ...)
    ///   // Character.GetBoundingBox(): Position + (8,16) 大小 48x32 → 脚底约 Position+48
    /// </code>
    /// 而我们的影子原本画在 <c>local + (32, 58)</c>、精灵脚底在 <c>local + 64</c> ——
    /// 整个人塌下去半格，所以站进草丛时身体正好落在草叶最密的那一段，被埋掉；
    /// 看起来还像"站进农舍台阶里"（其实是被房子的下半截盖住）。
    /// 上提 32 后，身体和原版人物落在同一高度：草丛只盖腿、房子不会吞人。
    /// </summary>
    private const float LiftY = 32f;

    /// <summary>能不能画：贴图就绪、AI 存在、且和玩家在同一张地图。</summary>
    private static bool Ready(Companion c, out Vector2 local)
    {
        local = Vector2.Zero;
        if (_sprite == null || c == null || !c.Active)
            return false;

        // AI 不在玩家当前地图时不绘制（否则会出现在农舍里等错误位置）
        if (c.Location != null && Game1.currentLocation != c.Location)
            return false;

        local = Game1.GlobalToLocal(Game1.viewport, c.Position);
        return true;
    }

    /// <summary>呼吸 / 行走 / 钓鱼使劲时的上下浮动（像素）。</summary>
    private static float BobOf(Companion c)
    {
        float bob = c.Sleeping ? 0f : (float)Math.Sin(c.BobPhase() / 180f) * 1.5f;

        // 钓鱼时加一点"使劲"的起伏：甩竿往前一顿、咬钩往下一沉
        if (c.Phase == FishPhase.抛竿)
            bob -= (float)Math.Sin(Math.PI * c.PhaseRatio(420.0)) * 3f;
        else if (c.Phase == FishPhase.咬钩)
            bob += (float)Math.Sin(Math.PI * c.PhaseRatio(450.0)) * 2.5f;

        return bob;
    }

    /// <summary>
    /// 画身体：影子 + 精灵 + 钓鱼特效。
    ///
    /// 入口不再是 RenderedWorld，而是原版 <c>GameLocation.drawFarmers</c> 的 Harmony 补丁
    /// （见 <see cref="DrawHook"/>）——也就是**和玩家同一层**：
    /// 后面原版还要画 objects（石头、树枝、杂草）和 drawAboveFrontLayer（草、作物、树），
    /// 这些东西会自然盖住他，效果和原版人物完全一致，而且每张贴图只画一次。
    /// </summary>
    public static void DrawBody(SpriteBatch b, Companion c)
    {
        if (!Ready(c, out Vector2 local))
            return;

        Vector2 drawBase = local + new Vector2(0f, -32f - LiftY);
        int walkPhase = (!c.Dead && !c.Sleeping && c.IsWalking)
            ? 1 + (int)(c.WalkAnimTime / 140.0) % 2
            : 0;
        Rectangle frame = FrameFor(c.Facing, walkPhase);

        // 影子
        b.Draw(_pixel, local + new Vector2(32f, 58f - LiftY), null, new Color(0, 0, 0, 70),
            0f, new Vector2(0.5f, 0.5f), new Vector2(52f, 18f), SpriteEffects.None, 0f);

        if (c.Dead)
        {
            b.Draw(_sprite, drawBase + new Vector2(32f, 32f), frame, Color.Gray,
                MathHelper.PiOver2, new Vector2(8f, 12f), 4f, SpriteEffects.None, 0.001f);
            return;
        }

        float bob = BobOf(c);

        b.Draw(_sprite, drawBase + new Vector2(0f, bob), frame, TintOf(c),
            0f, Vector2.Zero, 4f, SpriteEffects.None, 0.001f);

        // 挥击特效已按玩家要求移除（无论实心白条还是细挥痕，都会糊在人物身上不好看）；
        // 干活状态改由头顶的行动标签体现。

        // 钓鱼：鱼竿、飞出去的浮漂、水面涟漪、咬钩水花（分阶段播）
        DrawFishing(b, c, local);
    }

    /// <summary>
    /// 画头顶 UI：状态胶囊、名字、血条/体力条、脚下标签、交互提示。
    /// 走 SMAPI 的 RenderedWorld（整个世界画完之后），所以不会被草叶、作物、树冠盖掉。
    ///
    /// 「身体被作物遮挡」关掉时，身体也在这里一起画 —— 等于回到"永远压在最上层"的老行为。
    /// </summary>
    public static void DrawOverlay(SpriteBatch b, Companion c)
    {
        if (!Ready(c, out Vector2 local))
            return;

        // 被草/作物盖住时，上半身再画一次——否则站在草丛正中就只剩影子，
        // 玩家看不见自己雇的人（腿仍然留在草里）
        if (ModEntry.Config?.身体被作物遮挡 == true)
            DrawUpperBody(b, c);

        float bob = BobOf(c);

        // =================================================================
        // 头顶信息（2.3.1 重排）：
        //   ① 状态胶囊（带进度条）—— 只占一行，深色底，不再和名字压在一起
        //   ② 名字                  —— 胶囊与脑袋之间
        //   ③ 脚下：模式 + 背包       —— 合成一行
        //   ④ 交互提示              —— 放到最下面，不和状态挤
        // 之前是 状态/名字/[E]对话/（模式）/背包 共 5 行字叠在一起，全糊在脑袋上。
        // =================================================================
        Vector2 namePos = local + new Vector2(32f, -50f - LiftY + bob);

        string action = ActionLabel(c);
        Color labelColor = c.Phase == FishPhase.咬钩
            ? new Color(255, 150, 120)
            : new Color(255, 236, 170);
        float progress = (c.Job.Type == JobType.钓鱼
                          && c.Phase != FishPhase.无
                          && c.Phase != FishPhase.走去水边)
            ? c.FishOverallRatio
            : -1f;
        DrawStatusChip(b, action, local + new Vector2(32f, -94f - LiftY + bob), labelColor, progress);

        DrawCenteredText(b, Game1.dialogueFont, ModEntry.Config.AI名字, namePos, Color.White);

        if (c.Health < c.MaxHealth)
        {
            Vector2 barPos = namePos + new Vector2(-26f, 24f);
            b.Draw(_pixel, barPos, null, new Color(0, 0, 0, 170), 0f, Vector2.Zero,
                new Vector2(52f, 7f), SpriteEffects.None, 0.003f);
            float ratio = MathHelper.Clamp((float)c.Health / c.MaxHealth, 0f, 1f);
            Color barColor = ratio > 0.5f ? new Color(80, 200, 80)
                : ratio > 0.25f ? new Color(230, 200, 60)
                : new Color(220, 70, 60);
            b.Draw(_pixel, barPos + new Vector2(1f, 1f), null, barColor, 0f, Vector2.Zero,
                new Vector2(50f * ratio, 5f), SpriteEffects.None, 0.0031f);
        }

        // 体力条（2.4.0）：和血条同宽，紧贴在血条下面；体力满时不画，免得头顶多一条
        if (!c.Dead && c.EnergyRatio < 0.999f)
        {
            Vector2 sp = namePos + new Vector2(-26f, c.Health < c.MaxHealth ? 33f : 24f);
            b.Draw(_pixel, sp, null, new Color(0, 0, 0, 160), 0f, Vector2.Zero,
                new Vector2(52f, 5f), SpriteEffects.None, 0.003f);
            Color sc = c.EnergyRatio > 0.35f ? new Color(120, 205, 120) : new Color(230, 170, 70);
            b.Draw(_pixel, sp + new Vector2(1f, 1f), null, sc, 0f, Vector2.Zero,
                new Vector2(50f * c.EnergyRatio, 3f), SpriteEffects.None, 0.0031f);
        }

        if (c.Sleeping)
        {
            DrawCenteredText(b, Game1.smallFont, "Z z z",
                namePos + new Vector2(32f, -8f + (float)Math.Sin(c.BobPhase() / 300f) * 3f), Color.LightBlue);
        }

        // 脚下：模式 + 背包（一行）
        string foot = ModeLabel(c);
        if (!c.Dead && c.InventoryGroups > 0)
            foot += "　背包 " + c.InventoryGroups;
        DrawCenteredText(b, Game1.smallFont, foot, local + new Vector2(32f, 74f - LiftY),
            new Color(255, 255, 255, 190));

        // 交互提示单独放最下面一行
        if (!c.Dead && !c.Sleeping && ModEntry.Instance.IsPlayerNear(2.5f))
            DrawCenteredText(b, Game1.smallFont, "[" + ModEntry.Config.对话按键 + "] 对话",
                local + new Vector2(32f, 94f - LiftY), new Color(255, 255, 180, 235));
    }

    /// <summary>
    /// 头顶状态胶囊：深色圆角感底框 + 文字，可选底部进度条（-1 = 不画）。
    /// 比裸文字好读，也不会和名字/血条叠在一起。
    /// </summary>
    private static void DrawStatusChip(SpriteBatch b, string text, Vector2 center, Color textColor, float progress)
    {
        if (string.IsNullOrEmpty(text))
            return;

        SpriteFont font = Game1.smallFont;
        Vector2 size = font.MeasureString(text);
        int w = (int)size.X + 16;
        int h = (int)size.Y + 8 + (progress >= 0f ? 5 : 0);

        Vector2 topLeft = center - new Vector2(w / 2f, h / 2f);

        // 底框 + 上下细边
        b.Draw(_pixel, topLeft, null, new Color(18, 16, 26, 190), 0f, Vector2.Zero,
            new Vector2(w, h), SpriteEffects.None, 0.0032f);
        b.Draw(_pixel, topLeft, null, new Color(255, 255, 255, 50), 0f, Vector2.Zero,
            new Vector2(w, 1f), SpriteEffects.None, 0.0033f);
        b.Draw(_pixel, topLeft + new Vector2(0f, h - 1f), null, new Color(255, 255, 255, 50), 0f, Vector2.Zero,
            new Vector2(w, 1f), SpriteEffects.None, 0.0033f);

        // 文字
        Vector2 textPos = new Vector2(center.X - size.X / 2f, topLeft.Y + 4f);
        b.DrawString(font, text, textPos + new Vector2(1f, 1f), new Color(0, 0, 0, 180));
        b.DrawString(font, text, textPos, textColor);

        // 进度条（钓鱼时显示"在推进"）
        if (progress >= 0f)
        {
            float barW = w - 8f;
            Vector2 barPos = new Vector2(topLeft.X + 4f, topLeft.Y + h - 8f);
            b.Draw(_pixel, barPos, null, new Color(255, 255, 255, 45), 0f, Vector2.Zero,
                new Vector2(barW, 3f), SpriteEffects.None, 0.0034f);
            b.Draw(_pixel, barPos, null, new Color(120, 210, 255), 0f, Vector2.Zero,
                new Vector2(barW * MathHelper.Clamp(progress, 0f, 1f), 3f), SpriteEffects.None, 0.0035f);
        }
    }

    /// <summary>供 Companion 取"玩家那把斧头"用（原版砍倒动画需要真的 Tool 对象）。</summary>
    public static Tool FindTool(string id)
    {
        return GetTool(id);
    }

    // =====================================================================
    // 钓鱼绘制：鱼竿 / 浮漂（抛竿弧线 → 等待抖动 → 咬钩下沉 → 收竿回弹）
    // 之前只有一根静止的线和一块方块，玩家反馈"完全没动画"。
    // =====================================================================
    private static void DrawFishing(SpriteBatch b, Companion c, Vector2 local)
    {
        if (c.Phase == FishPhase.无 || c.Phase == FishPhase.走去水边)
            return;
        if (c.FishTile == Vector2.Zero)
            return;

        Vector2 hand = local + new Vector2(32f, 20f - LiftY);

        // 浮漂一律画在"四面都是水"的格上：岸边那一两格的贴图会盖着沙滩，
        // 落在那儿看着就是"鱼钩扔在岸上"（玩家连提两次）。当前格不是纯水面就往里找一格。
        Vector2 bobberTile = FindWaterSpriteTile(c.FishTile);
        Vector2 water = Game1.GlobalToLocal(Game1.viewport, bobberTile * 64f + new Vector2(32f, 32f));

        Vector2 toWater = water - hand;
        if (toWater.LengthSquared() < 1f)
            return;

        float baseAngle = (float)Math.Atan2(toWater.Y, toWater.X);
        const float rodLen = 34f;

        // 竿子角度：抛竿时从后上方甩到指向水面，等待时轻轻抖，咬钩往上提，收竿收回
        float swing;
        switch (c.Phase)
        {
            case FishPhase.抛竿:
                swing = MathHelper.ToRadians(-95f) * (1f - EaseOutCubic(c.PhaseRatio(420.0)));
                break;
            case FishPhase.等待:
                swing = (float)Math.Sin(c.Clock / 260.0) * 0.045f;
                break;
            case FishPhase.咬钩:
                swing = MathHelper.ToRadians(-20f) * (float)Math.Sin(Math.PI * c.PhaseRatio(450.0));
                break;
            default: // 收竿
                swing = MathHelper.ToRadians(-14f) * (1f - c.PhaseRatio(350.0));
                break;
        }

        float rodAngle = baseAngle + swing;
        Vector2 rodDir = new Vector2((float)Math.Cos(rodAngle), (float)Math.Sin(rodAngle));
        Vector2 rodTip = hand + rodDir * rodLen;

        // 浮漂位置
        Vector2 bobber;
        switch (c.Phase)
        {
            case FishPhase.抛竿:
            {
                float t = EaseOutCubic(c.PhaseRatio(420.0));
                bobber = Vector2.Lerp(rodTip, water, t);
                bobber.Y -= (float)Math.Sin(Math.PI * t) * 26f;   // 抛物线飞出去
                break;
            }
            case FishPhase.咬钩:
            {
                float t = c.PhaseRatio(450.0);
                bobber = water + new Vector2(0f, (float)Math.Sin(Math.PI * t) * 8f + 2f);
                break;
            }
            case FishPhase.收竿:
            {
                float t = EaseOutCubic(c.PhaseRatio(350.0));
                bobber = Vector2.Lerp(water, rodTip, t);
                break;
            }
            default: // 等待上钩
                bobber = water + new Vector2(0f, (float)Math.Sin(c.Clock / 170.0) * 1.5f + 2f);
                break;
        }

        // 鱼线
        Vector2 line = bobber - rodTip;
        float len = line.Length();
        if (len > 1f)
        {
            b.Draw(_pixel, rodTip, null, new Color(245, 245, 255, 120),
                (float)Math.Atan2(line.Y, line.X), new Vector2(0f, 0.5f),
                new Vector2(len, 1.2f), SpriteEffects.None, 0.002f);
        }

        // 水面涟漪：等鱼时一直扩散，让"等 9 秒"看起来是在钓鱼而不是死机
        if (c.Phase == FishPhase.等待 || c.Phase == FishPhase.咬钩)
        {
            for (int k = 0; k < 2; k++)
            {
                float phase = (float)((c.Clock / 1100.0 + k * 0.5) % 1.0);
                float radius = 5f + phase * 20f;
                byte alpha = (byte)(110 * (1f - phase));
                DrawEllipse(b, water, radius, radius * 0.45f,
                    new Color((byte)210, (byte)235, (byte)255, alpha), 1.2f);
            }
        }

        // 咬钩水花
        if (c.Phase == FishPhase.咬钩)
        {
            float t = c.PhaseRatio(450.0);
            byte alpha = (byte)(220 * (1f - t));
            for (int i = 0; i < 6; i++)
            {
                float ang = MathHelper.TwoPi * i / 6f;
                Vector2 dir = new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang) * 0.5f);
                Vector2 p = water + dir * (5f + t * 16f);
                b.Draw(_pixel, p, null, new Color((byte)255, (byte)255, (byte)255, alpha), ang,
                    new Vector2(0.5f, 0.5f), new Vector2(5f, 2f), SpriteEffects.None, 0.0023f);
            }
        }

        // 鱼竿
        b.Draw(_pixel, hand, null, new Color(122, 82, 48), rodAngle, new Vector2(0f, 0.5f),
            new Vector2(rodLen, 3f), SpriteEffects.None, 0.0024f);
        b.Draw(_pixel, hand, null, new Color(176, 126, 78), rodAngle, new Vector2(0f, 0.5f),
            new Vector2(rodLen * 0.9f, 1.2f), SpriteEffects.None, 0.0025f);
        // 线轮
        b.Draw(_pixel, hand + rodDir * 8f, null, new Color(70, 70, 80), 0f, new Vector2(0.5f, 0.5f),
            new Vector2(4f, 4f), SpriteEffects.None, 0.0026f);

        // 浮漂：上红下白
        b.Draw(_pixel, bobber + new Vector2(-3f, -7f), null, new Color(226, 72, 62), 0f, Vector2.Zero,
            new Vector2(6f, 5f), SpriteEffects.None, 0.0026f);
        b.Draw(_pixel, bobber + new Vector2(-3f, -2f), null, Color.White, 0f, Vector2.Zero,
            new Vector2(6f, 4f), SpriteEffects.None, 0.0027f);
    }

    /// <summary>
    /// 找一个"贴图是纯水面"的格来画浮漂：本身是水、且上下左右也都是水。
    /// 岸边格虽然 isWaterTile 为真，但贴图会画上沙滩/草岸，浮漂落上去像在岸上。
    /// 当前格不满足就在 3 格半径内找最近的（找不到就退回原格）。
    /// </summary>
    private static Vector2 FindWaterSpriteTile(Vector2 tile)
    {
        GameLocation loc = Game1.currentLocation;
        if (loc == null)
            return tile;
        if (IsPureWater(loc, (int)tile.X, (int)tile.Y))
            return tile;

        Vector2 best = tile;
        float bestDist = 3.2f;
        for (int dx = -3; dx <= 3; dx++)
        {
            for (int dy = -3; dy <= 3; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                int x = (int)tile.X + dx;
                int y = (int)tile.Y + dy;
                if (x < 0 || y < 0)
                    continue;
                if (!IsPureWater(loc, x, y))
                    continue;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = new Vector2(x, y);
                }
            }
        }
        return best;
    }

    /// <summary>这一格是水，且四邻都是水（贴图一定是纯水面）。</summary>
    private static bool IsPureWater(GameLocation loc, int x, int y)
    {
        return loc.isWaterTile(x, y)
            && loc.isWaterTile(x - 1, y)
            && loc.isWaterTile(x + 1, y)
            && loc.isWaterTile(x, y - 1)
            && loc.isWaterTile(x, y + 1);
    }

    private static float EaseOutCubic(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    /// <summary>用短线段拼一个椭圆（水面涟漪），不需要额外贴图。</summary>
    private static void DrawEllipse(SpriteBatch b, Vector2 center, float rx, float ry, Color color, float thickness)
    {
        const int segments = 18;
        for (int i = 0; i < segments; i++)
        {
            float ang = MathHelper.TwoPi * i / segments;
            Vector2 p = center + new Vector2((float)Math.Cos(ang) * rx, (float)Math.Sin(ang) * ry);
            float tangent = ang + MathHelper.PiOver2;
            float segLen = MathHelper.TwoPi * Math.Max(rx, ry) / segments * 0.9f;
            b.Draw(_pixel, p, null, color, tangent, new Vector2(0.5f, 0.5f),
                new Vector2(segLen, thickness), SpriteEffects.None, 0.0019f);
        }
    }

    private static string ModeLabel(Companion c)
    {
        if (c.Dead)
            return "（倒下）";
        if (!ModEntry.Instance.IsHired)
            return "（未雇佣）";
        return "（" + c.Mode + "）";
    }

    /// <summary>头顶行动标签：根据当前工作/状态返回中文短语。</summary>
    private static string ActionLabel(Companion c)
    {
        if (c.Dead)
            return "倒下了";
        if (c.Sleeping)
            return "睡觉中";
        if (!ModEntry.Instance.IsHired)
        {
            // 遣散后会自己走回去，走的路上一律写"回门口中"，别骗人说已经在门口
            float homeDist = Vector2.Distance(c.Tile, c.HomeTile);
            return homeDist > 1.5f ? "回门口中" : "在门口待命";
        }

        if (c.Job.Type == JobType.无)
        {
            if (c.Mode == AiMode.待命)
                return "待命中";
            return "";   // 只是跟着你时不挂胶囊（太吵），脚下那行已经写了模式
        }

        // 钓鱼按阶段区分：一直写"钓鱼中"会让人以为卡住了
        if (c.Job.Type == JobType.钓鱼)
            return FishLabel(c);

        return c.Job.Type switch
        {
            JobType.回家 => "回家中",
            JobType.打怪 => "战斗中",
            JobType.挖矿 => "挖矿中",
            JobType.砍树 => "砍树中",
            JobType.采集 => "采集中",
            JobType.清理 => "清理中",
            JobType.送背包 => "背包满了，去找你",
            JobType.存背包 => "背包满了，去塞箱子",
            JobType.收获 => "收割中",
            JobType.浇水 => "浇水中",
            JobType.翻地 => "翻地中",
            JobType.播种 => "播种中",
            _ => "干活中",
        };
    }

    private static string FishLabel(Companion c)
    {
        switch (c.Phase)
        {
            case FishPhase.走去水边:
                return "去水边";
            case FishPhase.抛竿:
                return "抛竿中";
            case FishPhase.等待:
                return "等鱼上钩 " + c.FishWaitSecondsLeft.ToString("0.0") + "s"
                    + (c.FishHadBait ? "" : "（无饵）");
            case FishPhase.咬钩:
                return "咬钩！";
            case FishPhase.收竿:
                return "收竿中";
            default:
                return "钓鱼中";
        }
    }

    private static void DrawCenteredText(SpriteBatch b, SpriteFont font, string text, Vector2 center, Color color)
    {
        if (string.IsNullOrEmpty(text))
            return;
        Vector2 size = font.MeasureString(text);
        Vector2 pos = center - new Vector2(size.X / 2f, size.Y / 2f);
        b.DrawString(font, text, pos + new Vector2(1f, 1f), new Color(0, 0, 0, 160));
        b.DrawString(font, text, pos, color);
    }
}
