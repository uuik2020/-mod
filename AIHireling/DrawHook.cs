using System;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace AIHireling;

/// <summary>
/// 把 AI 的**身体**插进原版绘制流程的中间，让他和玩家享有同样的遮挡关系。
///
/// 为什么非要插队：游戏画世界用的是 <c>SpriteSortMode.Deferred</c>（Game1.cs），
/// **layerDepth 根本不参与排序**，谁先画谁在下面。原版顺序是：
/// <code>
///   地形背景 → drawCharacters/drawFarmers(角色) → objects → drawAboveFrontLayer(草/作物/树)
/// </code>
/// 而我们在 SMAPI 的 <c>RenderedWorld</c> 里画，等于最后画，天生压在所有东西上面。
///
/// 2.6.3 试过"画完 AI 再把周围地形补画一遍"，结果是同一张贴图叠了两层：
/// 草叶从稀疏变成不透明，人直接被埋掉（玩家 2.6.3 截图的翻车现场）。
/// 现在改成挂在 <c>GameLocation.drawFarmers</c> 的 Postfix 上——正好是
/// "玩家刚画完、objects 和地形前景还没画"的位置，AI 就享受和玩家一模一样的遮挡，
/// 而且每张贴图只画一次，不会有叠加变浓的问题。
///
/// 头顶 UI（名字、血条、状态胶囊）仍然在 RenderedWorld 里画，免得被草叶糊掉。
/// </summary>
internal static class DrawHook
{
    private const string HarmonyId = "AIHireling.DrawLayer";

    private static Harmony _harmony;

    /// <summary>补丁有没有挂上。失败时上层会退回"永远画在最上层"的老路径。</summary>
    public static bool Applied { get; private set; }

    /// <summary>在 Entry 里调一次。</summary>
    public static void Apply()
    {
        try
        {
            MethodInfo target = AccessTools.Method(typeof(GameLocation), "drawFarmers");
            if (target == null)
            {
                CLog.Warn("没找到 GameLocation.drawFarmers，AI 会画在所有东西上面（其它功能不受影响）");
                return;
            }

            MethodInfo postfix = AccessTools.Method(typeof(DrawHook), nameof(AfterDrawFarmers));
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Applied = true;
            CLog.Ok("身体遮挡已接进原版绘制顺序（玩家 → AI → 物件/草地/作物/树）");
        }
        catch (Exception ex)
        {
            Applied = false;
            CLog.Warn("接进原版绘制顺序失败，AI 会画在所有东西上面：" + ex.Message);
        }
    }

    /// <summary>
    /// 玩家画完之后、物件与地形前景之前，画 AI 的身体。
    /// 之后的 objects（石头、树枝、杂草）和 drawAboveFrontLayer（草、作物、树）
    /// 由原版接着画，自然就把 AI 盖住了。
    /// </summary>
    private static void AfterDrawFarmers(GameLocation __instance, SpriteBatch b)
    {
        try
        {
            if (__instance == null || __instance != Game1.currentLocation)
                return;
            if (ModEntry.Config?.身体被作物遮挡 != true)
                return;
            if (__instance.shouldHideCharacters())
                return;                     // 过场等需要隐藏角色的场合，AI 也一起藏
            if (Game1.eventUp && (Game1.CurrentEvent == null || !Game1.CurrentEvent.showWorldCharacters))
                return;                     // 和原版 drawCharacters 的判断保持一致

            Companion c = ModEntry.Companion;
            if (c == null || !c.Active)
                return;
            if (c.Location != null && c.Location != __instance)
                return;

            CompanionRenderer.DrawBody(b, c);
        }
        catch
        {
            // 渲染出问题绝不能把游戏搞崩
        }
    }
}
