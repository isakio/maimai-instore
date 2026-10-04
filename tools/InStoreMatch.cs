// InStoreMatch v2 —— 选曲界面「店内マッチング」分类诊断 / 修复插件
//
// 【本轮结论（反汇编 Assembly-CSharp.dll 得到，已确认）】
//
// 底部那一排分类标签 = Monitor.MusicSelectMonitor._genreTabController
//   → .TabController → .SelectorTab，真正画格子的字段是 .SelectorTab._tabDatas
//   （List<TabDataBase>）—— 这才是之前一直找不到的「第三份数据」。
//
// _tabDatas 只在 MusicSelectMonitor.OnStartMusicSelect() 里被生成一次：
//   foreach (data in MusicSelectProcess.GenreSelectDataList[monitor])
//       tabDatas.Add(new TabDataBase(getTabColor(data), GetTabSprite(data),
//                                    getTabString(data), ""));
//   _genreTabController.SortType2Genre(tabDatas, 0);   // -> _tab.SetData(tabDatas)
//   _genreTabController.Change(CurrentCategorySelect);
//
// 也就是说它是一张「快照」，不读 CategoryNameList、也不实时读 _genreSelectDataList。
//
// NyanLink 的 198（店内マッチング）是玩家进入选曲界面之后才异步塞进
// GenreSelectDataList 的（PartyExec -> reinputConnectCombineData -> InputGenreSelectData(198)），
// 那时快照早拍完了，而且没有任何代码会重拍 —— 所以标签栏永远少这一格。
// 这也解释了：_currentExtraCategoryCount 一直等于 4（拍快照时确实只有 4 个额外分类），
// 以及之前「补名字 / +1 计数 / 重放 ExtraMusic / 挪到第 0 位」都没用（那些都不碰 _tabDatas）。
//
// 【本插件做什么】
//   1. 诊断：打印 GenreSelectDataList 每项 categoryID，以及标签栏 _tabDatas 的真实内容
//   2. 修复：一旦 GenreSelectDataList 里出现 198，就照游戏自己的方式重拍 _tabDatas
//      （build List<TabDataBase> -> SortType2Genre(list, 0) -> Change(CurrentCategorySelect)）
//   3. 修复：CategoryNameList 少一项时补上「店内マッチング」。
//      因为 MusicSelectSequence.CategoryScrollRight/Left 用 CategoryNameList.Count 当滚动边界，
//      不补的话最末一格（198）永远滚不到。
//   4. 兜底：每帧对一次账，项数对不上就自动重拍
//   5. 验证：第一次对账通过后，把 CurrentCategorySelect 直接切到 198 并调
//      Monitor.SetDeployList(0,0) 刷新（省得手动滚 16 下）。如果屏幕跟着跳到
//      「店内マッチング」，就说明标签栏认这一格；如果屏幕没动，说明画的不是这个对象。
//      另外会打印 CategoryNameList / CurrentCategorySelect 便于对账。
//
// 编译用 Windows 自带 csc.exe（C# 5），不能用字符串插值 / nameof / 表达式体成员。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;          // Color / Sprite
using Process;              // MusicSelectProcess

[assembly: MelonInfo(typeof(InStoreMatch.InStoreMatchMod), "InStoreMatch", "2.5.0", "isakio")]
[assembly: MelonGame("sega-interactive", "Sinmai")]

namespace InStoreMatch
{
    public class InStoreMatchMod : MelonMod
    {
        // 玩法开关：招募出现时自动把分类切到「店内マッチング」。
        // 默认关：自动跳有点唐突，现在分类栏/面板都能手动切过去（v2.3/v2.4 修好的箭头）。
        private static readonly bool AutoJump = false;

        // 玩法开关：让「ジャンル」面板中间也出现「店内マッチング」的大卡片。
        // 游戏自己会把它删掉（GenreSelectChainList.SetObjectData 里有
        //  GetCategoryName(...) == GetMusicGenre(198).genreName → data=null → Remove(card)），
        // 这里用零宽空格把那个相等判断弄不相等（屏幕上看着一模一样）。
        private static readonly bool ShowGenreCard = true;

        // 玩法开关：「ジャンル」面板里，站在倒数第二格也能往右切到最后一格。
        // 本体公式 GenreSelectSequence.CheckButton: SetVisibleButton(CurrentCategorySelect+1 != count, Button02)，
        // 非 freedom 模式 count 还会 -1，于是站在 オンゲキ（倒数第二格）时右箭头被藏掉。
        private static readonly bool ShowRightArrow = true;

        // 玩法开关：「店内マッチング」分类里也显示 BACK。
        // 本体：MusicSelectSequence.IsBackEnable() => !IsConnectionFolder(0)（联机分类里故意不给）
        private static readonly bool ShowBackButton = true;

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("[InStoreMatch] v2.5 已加载（重拍标签栏 _tabDatas + 补滚动边界；AutoJump="
                + AutoJump + "，ShowGenreCard=" + ShowGenreCard
                + "，ShowRightArrow=" + ShowRightArrow
                + "，ShowBackButton=" + ShowBackButton + "）");
            PatchOne(typeof(PatchReinput), "reinputConnectCombineData");
            PatchOne(typeof(PatchSetConnectData), "SetConnectData");
            PatchOne(typeof(PatchOnUpdate), "OnUpdate");
            PatchOne(typeof(PatchGetCategoryName), "GetCategoryName");
            PatchOne(typeof(PatchGenreCheckButton), "GenreSelectSequence.CheckButton");
            PatchOne(typeof(PatchGenreUpdate), "GenreSelectSequence.Update");
            PatchOne(typeof(PatchBackEnable), "MusicSelectSequence.IsBackEnable");
        }

        private static void PatchOne(Type t, string label)
        {
            try
            {
                HarmonyLib.Harmony.CreateAndPatchAll(t);
                MelonLogger.Msg("[InStoreMatch] 挂钩成功: " + label);
            }
            catch (Exception e)
            {
                MelonLogger.Msg("[InStoreMatch] 挂钩失败 " + label + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------- 反射 helper

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            for (Type cur = o.GetType(); cur != null; cur = cur.BaseType)
            {
                try
                {
                    PropertyInfo p = cur.GetProperty(name, BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.DeclaredOnly);
                    if (p != null) return p.GetValue(o, null);
                }
                catch (Exception) { }
            }
            return null;
        }

        private static object Field(object o, string name)
        {
            if (o == null) return null;
            for (Type cur = o.GetType(); cur != null; cur = cur.BaseType)
            {
                try
                {
                    FieldInfo f = cur.GetField(name, BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.DeclaredOnly);
                    if (f != null) return f.GetValue(o);
                }
                catch (Exception) { }
            }
            return null;
        }

        private static bool SetProp(object o, string name, object value)
        {
            if (o == null) return false;
            for (Type cur = o.GetType(); cur != null; cur = cur.BaseType)
            {
                try
                {
                    PropertyInfo p = cur.GetProperty(name, BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.Instance |
                        BindingFlags.DeclaredOnly);
                    if (p != null && p.CanWrite)
                    {
                        p.SetValue(o, value, null);
                        return true;
                    }
                }
                catch (Exception) { }
            }
            return false;
        }

        // 私有方法在派生类上 GetMethod 找不到，必须自己往基类走
        private static MethodInfo FindByName(Type t, string name, int argc)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                MethodInfo[] ms = cur.GetMethods(BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.DeclaredOnly);
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i].Name == name && ms[i].GetParameters().Length == argc)
                        return ms[i];
                }
            }
            return null;
        }

        private static int CategoryIdOf(object item)
        {
            object v = Field(item, "categoryID");
            if (v == null) return int.MinValue;
            try { return Convert.ToInt32(v); }
            catch (Exception) { return int.MinValue; }
        }

        private static int CountOf(object list)
        {
            if (list == null) return -1;
            try
            {
                PropertyInfo cp = list.GetType().GetProperty("Count");
                if (cp != null) return (int)cp.GetValue(list, null);
            }
            catch (Exception) { }
            return -1;
        }

        // ---------------------------------------------------------- 诊断

        private static void Dump(MusicSelectProcess p)
        {
            try
            {
                int connectCount = CountOf(Field(p, "_connectCombineMusicDataList"));
                Array arr = Prop(p, "GenreSelectDataList") as Array;

                if (arr == null || arr.Length == 0)
                {
                    MelonLogger.Msg("[InStoreMatch] GenreSelectDataList 为空");
                    return;
                }

                for (int i = 0; i < arr.Length; i++)
                {
                    IList sub = arr.GetValue(i) as IList;
                    if (sub == null) continue;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int k = 0; k < sub.Count; k++)
                    {
                        sb.Append(CategoryIdOf(sub[k]));
                        if (k != sub.Count - 1) sb.Append(",");
                    }
                    MelonLogger.Msg("[InStoreMatch] 监视器" + i + " genre数据 " + sub.Count
                        + " 项 -> " + sb.ToString());
                }

                IList names = Prop(p, "CategoryNameList") as IList;
                object curSel = Prop(p, "CurrentCategorySelect");
                MelonLogger.Msg("[InStoreMatch] connectList=" + connectCount
                    + " CategoryNameList=" + (names == null ? -1 : names.Count)
                    + " CurrentCategorySelect=" + (curSel == null ? "?" : curSel.ToString())
                    + " connectEnable=" + (Prop(p, "IsConnectCategoryEnable") == null
                        ? "?" : Prop(p, "IsConnectCategoryEnable").ToString()));
                DumpTabBar(p);
            }
            catch (Exception e)
            {
                MelonLogger.Msg("[InStoreMatch] 状态输出失败: " + e.Message);
            }
        }

        // 打印真正画出来的标签栏内容
        private static void DumpTabBar(MusicSelectProcess p)
        {
            Array monitors = Prop(p, "MonitorArray") as Array;
            if (monitors == null) return;

            for (int i = 0; i < monitors.Length; i++)
            {
                object mon = monitors.GetValue(i);
                object tabCtrl = Field(mon, "_genreTabController");
                object selTab = Field(tabCtrl, "_tab");
                if (selTab == null)
                {
                    MelonLogger.Msg("[InStoreMatch] 监视器" + i + " 标签栏对象为空");
                    continue;
                }
                IList datas = Field(selTab, "_tabDatas") as IList;
                if (datas == null)
                {
                    MelonLogger.Msg("[InStoreMatch] 监视器" + i + " _tabDatas=null");
                    continue;
                }
                Array left = Field(selTab, "_leftPanels") as Array;
                Array right = Field(selTab, "_rightPanels") as Array;
                int lc = left == null ? -1 : left.Length;
                int rc = right == null ? -1 : right.Length;

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int k = 0; k < datas.Count && k < 40; k++)
                {
                    object d = datas[k];
                    object title = Prop(d, "Title");
                    object sprite = Prop(d, "TitleSprite");
                    sb.Append(title == null ? "?" : OneLine(title.ToString()));
                    sb.Append(sprite == null ? "[无图]" : "[有图]");
                    if (k != datas.Count - 1) sb.Append(" | ");
                }
                MelonLogger.Msg("[InStoreMatch] 监视器" + i + " 标签栏 _tabDatas="
                    + datas.Count + " 项，左" + lc + "/右" + rc + " -> " + sb.ToString());

                // 把每格「实际显示的文字」读回来，确认屏幕上的窗口停在哪
                System.Text.StringBuilder scr = new System.Text.StringBuilder();
                if (left != null)
                    for (int k = 0; k < left.Length; k++)
                        scr.Append(PanelText(left.GetValue(k))).Append(" | ");
                object mainPanel = Field(selTab, "_main");
                scr.Append("【").Append(PanelText(mainPanel));
                scr.Append("/").Append(PanelSprite(mainPanel)).Append("】");
                if (right != null)
                    for (int k = 0; k < right.Length; k++)
                        scr.Append(" | ").Append(PanelText(right.GetValue(k)));
                MelonLogger.Msg("[InStoreMatch] 监视器" + i + " 屏幕标签栏: " + scr.ToString());
            }
        }

        // MiniTabPanel._categoryNameText / MainTabPanel._subTitleText
        private static string PanelText(object panel)
        {
            if (panel == null) return "-";
            object t = Field(panel, "_categoryNameText");
            if (t == null) t = Field(panel, "_subTitleText");
            if (t == null) return "?";
            object v = Prop(t, "text");
            return v == null ? "?" : OneLine(v.ToString());
        }

        // 分类名里带换行（ゲーム＆\nバラエティ），不清掉的话日志会被劈成好几行
        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\r", " ").Replace("\n", " ");
        }

        // 中央格子真正显示的图（MainTabPanel._titleImage.sprite.name）
        private static string PanelSprite(object panel)
        {
            if (panel == null) return "-";
            object img = Field(panel, "_titleImage");
            if (img == null) img = Field(panel, "_bgImage");
            if (img == null) return "?";
            object sp = Prop(img, "sprite");
            if (sp == null) return "(null)";
            object nm = Prop(sp, "name");
            return nm == null ? "(sprite)" : nm.ToString();
        }

        // ---------------------------------------------------------- 修复：重拍标签栏

        private static int _lastTabCount = -2;
        private static int _tick;
        private static bool _dumpedOnce;

        private static bool HasConnectGenre(MusicSelectProcess p)
        {
            Array arr = Prop(p, "GenreSelectDataList") as Array;
            if (arr == null || arr.Length == 0) return false;
            IList sub = arr.GetValue(0) as IList;
            if (sub == null) return false;
            for (int i = 0; i < sub.Count; i++)
                if (CategoryIdOf(sub[i]) == 198) return true;
            return false;
        }

        private static int TabDataCount(MusicSelectProcess p)
        {
            Array monitors = Prop(p, "MonitorArray") as Array;
            if (monitors == null || monitors.Length == 0) return -1;
            object mon = monitors.GetValue(0);
            object selTab = Field(Field(mon, "_genreTabController"), "_tab");
            return CountOf(Field(selTab, "_tabDatas"));
        }

        // 照 OnStartMusicSelect 的做法重建标签栏
        private static int RebuildAll(MusicSelectProcess p)
        {
            Array monitors = Prop(p, "MonitorArray") as Array;
            Array genreLists = Prop(p, "GenreSelectDataList") as Array;
            if (monitors == null || genreLists == null) return 0;

            int cur = 0;
            object curObj = Prop(p, "CurrentCategorySelect");
            if (curObj != null) { try { cur = Convert.ToInt32(curObj); } catch (Exception) { } }

            int done = 0;
            int n = Math.Min(monitors.Length, genreLists.Length);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    object mon = monitors.GetValue(i);
                    if (mon == null) continue;

                    object active = Field(mon, "isPlayerActive");
                    if (active is bool && !(bool)active) continue;   // 只重建在用的监视器

                    IList data = genreLists.GetValue(i) as IList;
                    if (data == null || data.Count == 0) continue;

                    object tabCtrl = Field(mon, "_genreTabController");
                    if (tabCtrl == null) continue;

                    // 列表元素类型直接沿用现有 _tabDatas，保证反射调用类型完全对得上
                    IList existing = Field(Field(tabCtrl, "_tab"), "_tabDatas") as IList;
                    Type listType = existing != null ? existing.GetType() : null;
                    Type tabDataType = null;
                    if (listType != null && listType.IsGenericType)
                    {
                        Type[] ga = listType.GetGenericArguments();
                        if (ga.Length == 1) tabDataType = ga[0];
                    }
                    if (tabDataType == null)
                        tabDataType = typeof(MusicSelectProcess).Assembly.GetType("TabDataBase");
                    if (tabDataType == null)
                    {
                        MelonLogger.Msg("[InStoreMatch] 找不到 TabDataBase 类型");
                        return 0;
                    }
                    if (listType == null)
                        listType = typeof(List<>).MakeGenericType(new Type[] { tabDataType });

                    Type monType = mon.GetType();
                    MethodInfo mSprite = FindByName(monType, "GetTabSprite", 1);
                    MethodInfo mString = FindByName(monType, "getTabString", 1);
                    MethodInfo mColor = FindByName(monType, "getTabColor", 1);
                    MethodInfo mSort = FindByName(tabCtrl.GetType(), "SortType2Genre", 2);
                    MethodInfo mChange = FindByName(tabCtrl.GetType(), "Change", 1);

                    IList tabDatas = (IList)Activator.CreateInstance(listType);
                    for (int k = 0; k < data.Count; k++)
                    {
                        object gsd = data[k];
                        if (gsd == null) continue;
                        object[] one = new object[] { gsd };
                        object sprite = mSprite == null ? null : mSprite.Invoke(mon, one);
                        string title = mString == null ? "" : (string)mString.Invoke(mon, one);
                        object color = mColor == null ? null : mColor.Invoke(mon, one);
                        if (color == null) color = new Color(1f, 1f, 1f, 1f);
                        object td = Activator.CreateInstance(tabDataType,
                            new object[] { color, sprite, title, "" });
                        tabDatas.Add(td);
                    }

                    if (mSort != null) mSort.Invoke(tabCtrl, new object[] { tabDatas, 0 });
                    if (mChange != null)
                    {
                        int idx = cur;
                        if (idx < 0 || idx >= tabDatas.Count) idx = 0;
                        mChange.Invoke(tabCtrl, new object[] { idx });
                    }
                    done++;
                }
                catch (Exception e)
                {
                    MelonLogger.Msg("[InStoreMatch] 监视器" + i + " 重建失败: " + e.Message);
                }
            }
            return done;
        }

        private static int _lastAttemptTick = -1000;

        // 取「店内マッチング」的官方名字（CategoryNameList 里补的就是它）
        private static string _connectName;

        private static string ConnectName()
        {
            if (string.IsNullOrEmpty(_connectName))
            {
                _connectName = ConnectGenreName();
                if (string.IsNullOrEmpty(_connectName)) _connectName = "店内マッチング";
            }
            return _connectName;
        }

        private static string ConnectGenreName()
        {
            try
            {
                Type dm = typeof(MusicSelectProcess).Assembly.GetType("Manager.DataManager");
                object mgr = null;
                for (Type cur = dm; cur != null && mgr == null; cur = cur.BaseType)
                {
                    PropertyInfo pi = cur.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (pi != null)
                    {
                        try { mgr = pi.GetValue(null, null); }
                        catch (Exception) { }
                    }
                    if (mgr == null)
                    {
                        MethodInfo[] ms = cur.GetMethods(BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.Static |
                            BindingFlags.DeclaredOnly);
                        for (int i = 0; i < ms.Length; i++)
                        {
                            if (ms[i].Name == "get_Instance" &&
                                ms[i].GetParameters().Length == 0)
                            {
                                try { mgr = ms[i].Invoke(null, null); }
                                catch (Exception) { }
                                break;
                            }
                        }
                    }
                }
                if (mgr == null) return null;

                MethodInfo gmg = null;
                MethodInfo[] gm = mgr.GetType().GetMethods(BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < gm.Length; i++)
                {
                    ParameterInfo[] ps = gm[i].GetParameters();
                    if (gm[i].Name == "GetMusicGenre" && ps.Length == 1 &&
                        ps[0].ParameterType == typeof(int)) { gmg = gm[i]; break; }
                }
                if (gmg == null) return null;

                object genre = gmg.Invoke(mgr, new object[] { 198 });
                if (genre == null) return null;
                PropertyInfo np = genre.GetType().GetProperty("genreName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (np != null)
                {
                    string s = np.GetValue(genre, null) as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception) { }
            return null;
        }

        // CategoryNameList 的长度被 CategoryScrollRight/Left 当作滚动边界。
        // NyanLink 事后把 198 塞进 GenreSelectDataList 时没同步补名字，
        // 导致最后一格滚不到。这里按差值补上（正常情况就是少 1 项）。
        private static void EnsureConnectName(MusicSelectProcess p, int genreCount)
        {
            try
            {
                IList names = Prop(p, "CategoryNameList") as IList;
                if (names == null || genreCount <= 0) return;
                if (names.Count >= genreCount) return;
                int need = genreCount - names.Count;
                if (need > 4)
                {
                    MelonLogger.Msg("[InStoreMatch] CategoryNameList 只差得太多（"
                        + names.Count + " vs " + genreCount + "），跳过不补");
                    return;
                }
                string nm = ConnectName();
                for (int k = 0; k < need; k++) names.Add(nm);
                MelonLogger.Msg("[InStoreMatch] CategoryNameList 补齐 " + need + " 项 -> "
                    + names.Count + " 项（滚动边界现在能到最后）");
            }
            catch (Exception e)
            {
                MelonLogger.Msg("[InStoreMatch] EnsureConnectName 失败: " + e.Message);
            }
        }

        // 只有当 genre 数据里真的有 198、且标签栏项数对不上时才重拍
        private static bool _jumped;

        // 诊断用：把当前分类切到「店内マッチング」，验证标签栏到底认不认这一格
        private static void TryJumpToConnect(MusicSelectProcess p)
        {
            if (!AutoJump || _jumped) return;
            try
            {
                Array arr = Prop(p, "GenreSelectDataList") as Array;
                if (arr == null || arr.Length == 0) return;
                IList sub = arr.GetValue(0) as IList;
                if (sub == null) return;

                int idx = -1;
                for (int k = 0; k < sub.Count; k++)
                    if (CategoryIdOf(sub[k]) == 198) { idx = k; break; }
                if (idx < 0) return;

                Array monitors = Prop(p, "MonitorArray") as Array;
                if (monitors == null || monitors.Length == 0) return;
                object mon = monitors.GetValue(0);
                object active = Field(mon, "isPlayerActive");
                if (active is bool && !(bool)active) return;

                if (!SetProp(p, "CurrentCategorySelect", idx)) return;
                _jumped = true;

                // SetDeployList(0,0) = 游戏自己的「按当前分类重铺音乐列表 + 刷新标签栏」
                MethodInfo deploy = FindByName(mon.GetType(), "SetDeployList", 2);
                if (deploy != null)
                {
                    // 它是 SetDeployList(bool, bool)，按签名塞参数，别写死 int
                    ParameterInfo[] ps = deploy.GetParameters();
                    object[] dargs = new object[ps.Length];
                    for (int q = 0; q < ps.Length; q++)
                    {
                        Type pt = ps[q].ParameterType;
                        if (pt == typeof(bool)) dargs[q] = false;
                        else if (pt == typeof(int)) dargs[q] = 0;
                        else if (pt.IsEnum) dargs[q] = 0;
                        else dargs[q] = null;
                    }
                    deploy.Invoke(mon, dargs);
                }

                MelonLogger.Msg("[InStoreMatch] 已强制切入「店内マッチング」：CurrentCategorySelect="
                    + idx + " / " + (sub.Count - 1) + "，SetDeployList="
                    + (deploy != null ? "ok" : "找不到"));
                DumpTabBar(p);
            }
            catch (Exception e)
            {
                MelonLogger.Msg("[InStoreMatch] TryJumpToConnect 失败: " + e.Message);
                _jumped = true;
            }
        }

        private static void MaybeRebuild(MusicSelectProcess p, string why, bool verbose)
        {
            try
            {
                if (!HasConnectGenre(p)) return;

                Array arr = Prop(p, "GenreSelectDataList") as Array;
                IList sub = arr == null ? null : arr.GetValue(0) as IList;
                int genreCount = sub == null ? -1 : sub.Count;
                int tabCount = TabDataCount(p);

                EnsureConnectName(p, genreCount);

                if (tabCount == genreCount)
                {
                    _lastTabCount = tabCount;
                    TryJumpToConnect(p);
                    return;
                }

                // 对账没通过也不能每帧刷；最多每 30 帧试一次
                if (!verbose && _tick - _lastAttemptTick < 30) return;
                _lastAttemptTick = _tick;

                int done = RebuildAll(p);
                _lastTabCount = TabDataCount(p);
                IList nm = Prop(p, "CategoryNameList") as IList;
                object cs = Prop(p, "CurrentCategorySelect");
                MelonLogger.Msg("[InStoreMatch] 重拍标签栏(" + why + ")：genre=" + genreCount
                    + " CategoryNameList=" + (nm == null ? -1 : nm.Count)
                    + " CurrentCategorySelect=" + (cs == null ? "?" : cs.ToString())
                    + "，重建 " + done + " 个监视器，重建后 _tabDatas="
                    + _lastTabCount + " 项");
                if (verbose) DumpTabBar(p);
                TryJumpToConnect(p);
            }
            catch (Exception e)
            {
                MelonLogger.Msg("[InStoreMatch] MaybeRebuild 失败: " + e.Message);
            }
        }

        // ---------------------------------------------------------- 补丁

        // 连接数据一变（PartyExec -> reinputConnectCombineData），立刻重拍标签栏
        [HarmonyPatch(typeof(MusicSelectProcess), "reinputConnectCombineData")]
        public static class PatchReinput
        {
            public static void Postfix(MusicSelectProcess __instance)
            {
                MaybeRebuild(__instance, "reinputConnectCombineData", true);
            }
        }

        [HarmonyPatch(typeof(MusicSelectProcess), "SetConnectData")]
        public static class PatchSetConnectData
        {
            public static void Postfix(MusicSelectProcess __instance)
            {
                MaybeRebuild(__instance, "SetConnectData", false);
            }
        }

        // 兜底对账 + 诊断输出
        [HarmonyPatch(typeof(MusicSelectProcess), "OnUpdate")]
        public static class PatchOnUpdate
        {
            public static void Postfix(MusicSelectProcess __instance)
            {
                _tick++;
                MaybeRebuild(__instance, "定时对账", false);

                // 第一次尽快输出，之后每 30 秒一次，避免刷屏
                if (!_dumpedOnce || _tick % 1800 == 0)
                {
                    _dumpedOnce = true;
                    Dump(__instance);
                }
            }
        }

        // 让「ジャンル」面板别把「店内マッチング」那张大卡片删掉：
        // 游戏判等用的是 GetCategoryName() 的返回值，我们在后面缀一个零宽空格，
        // 屏幕上看不出区别，但相等判断不再成立 → 卡片会被正常生成。
        [HarmonyPatch(typeof(MusicSelectProcess), "GetCategoryName")]
        public static class PatchGetCategoryName
        {
            private static bool _logged;

            public static void Postfix(ref string __result)
            {
                if (!ShowGenreCard || string.IsNullOrEmpty(__result)) return;
                try
                {
                    if (__result != ConnectName()) return;
                    __result = __result + "\u200b";
                    if (!_logged)
                    {
                        _logged = true;
                        MelonLogger.Msg("[InStoreMatch] 已放开「店内マッチング」大卡片（GetCategoryName 加零宽空格）");
                    }
                }
                catch (Exception) { }
            }
        }

        // 手动设置某个按钮的显隐（id 见全局枚举 ButtonSetting：2=右箭头 3=NEXT 4=BACK 5=左箭头）
        private static void SetButton(object proc, int buttonId, bool visible)
        {
            Array monitors = Prop(proc, "MonitorArray") as Array;
            if (monitors == null) return;
            for (int i = 0; i < monitors.Length; i++)
            {
                object mon = monitors.GetValue(i);
                if (mon == null) continue;
                MethodInfo mi = FindByName(mon.GetType(), "SetVisibleButton", 2);
                if (mi == null) continue;
                ParameterInfo[] ps = mi.GetParameters();
                object[] args = new object[ps.Length];
                for (int q = 0; q < ps.Length; q++)
                {
                    Type pt = ps[q].ParameterType;
                    if (pt == typeof(bool)) args[q] = visible;
                    else if (pt.IsEnum) args[q] = Enum.ToObject(pt, buttonId);
                    else if (pt == typeof(int)) args[q] = buttonId;
                    else args[q] = null;
                }
                mi.Invoke(mon, args);
            }
        }

        // 「ジャンル」面板的右箭头：本体公式用的是 CombineMusicDataList.Count，
        // 非 freedom 模式还会 -1，导致「倒数第二格藏箭头 / 最后一格反而显示」。
        // 这里按真实分类数重算：还有下一格才给右箭头，没有就明确收掉。
        [HarmonyPatch(typeof(Process.SubSequence.GenreSelectSequence), "CheckButton")]
        public static class PatchGenreCheckButton
        {
            public static void Postfix(Process.SubSequence.GenreSelectSequence __instance)
            {
                if (!ShowRightArrow) return;
                try
                {
                    object proc = Field(__instance, "ProcessProcessing");
                    if (proc == null) return;
                    int realCount = CountOf(Prop(proc, "CombineMusicDataList"));
                    if (realCount <= 0) return;
                    object curObj = Prop(proc, "CurrentCategorySelect");
                    int cur = curObj == null ? 0 : Convert.ToInt32(curObj);
                    SetButton(proc, 2, cur + 1 < realCount);
                }
                catch (Exception) { }
            }
        }

        // 光把箭头显示出来还不够：按下右键的处理在 GenreSelectSequence.Update 里，
        // 那里有一条同样的判断
        //     count = CombineMusicDataList.Count;  if (!IsFreedomMode) count--;
        //     if (CurrentCategorySelect + 1 >= count) 直接跳过
        // 所以站在倒数第二格按右键会被吃掉。这两处用到的 freedom 判断只在这个
        // Update 里出现，于是临时把 GameManager.IsFreedomMode 当成 true 用一次，
        // 方法结束（无论正常还是异常）立刻还原。
        [HarmonyPatch(typeof(Process.SubSequence.GenreSelectSequence), "Update")]
        public static class PatchGenreUpdate
        {
            private static FieldInfo _freedom;
            private static bool _flipped;
            private static bool _logged;

            public static void Prefix()
            {
                if (!ShowRightArrow) return;
                try
                {
                    if (_freedom == null)
                    {
                        Type gm = typeof(Process.MusicSelectProcess).Assembly
                            .GetType("Manager.GameManager");
                        if (gm != null)
                            _freedom = gm.GetField("<IsFreedomMode>k__BackingField",
                                BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Static);
                    }
                    if (_freedom == null) return;
                    object v = _freedom.GetValue(null);
                    if (v is bool && !(bool)v)
                    {
                        _freedom.SetValue(null, true);
                        _flipped = true;
                        if (!_logged)
                        {
                            _logged = true;
                            MelonLogger.Msg("[InStoreMatch] 面板右键已改按真实分类数判断"
                                + "（本次 Update 临时借用 freedom 分支）");
                        }
                    }
                }
                catch (Exception) { }
            }

            // Finalizer 保证异常时也能还原
            public static Exception Finalizer(Exception __exception)
            {
                if (_flipped)
                {
                    try { if (_freedom != null) _freedom.SetValue(null, false); }
                    catch (Exception) { }
                    _flipped = false;
                }
                return __exception;
            }
        }

        // 「店内マッチング」分类里把 BACK 补回来（本体故意不给）
        [HarmonyPatch(typeof(Process.SubSequence.MusicSelectSequence), "IsBackEnable")]
        public static class PatchBackEnable
        {
            public static void Postfix(Process.SubSequence.MusicSelectSequence __instance, ref bool __result)
            {
                if (!ShowBackButton || __result) return;
                try
                {
                    object proc = Field(__instance, "ProcessProcessing");
                    if (proc == null) return;
                    MethodInfo isConn = FindByName(proc.GetType(), "IsConnectionFolder", 1);
                    if (isConn == null) return;
                    object r = isConn.Invoke(proc, new object[] { 0 });
                    if (r is bool && (bool)r)
                    {
                        __result = true;
                        if (!_backLogged)
                        {
                            _backLogged = true;
                            MelonLogger.Msg("[InStoreMatch] 已在「店内マッチング」里放回 BACK 按钮");
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        private static bool _backLogged;
    }
}
