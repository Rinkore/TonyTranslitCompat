# 东尼算法 × 音译尖塔 兼容修复

让东尼算法生成的卡牌，也用音译尖塔的方式说话。

---

## 问题

音译尖塔（TransliterateTheSpire）的工作方式是**覆盖游戏的本地化表**。
原版卡牌、遗物、关键词都会经过它，所以显示为音译。

但东尼算法（Auto-Anthonyology）随机生成的卡牌不一样：
它的卡牌描述、卡名、混沌能力文本是**运行时自己拼出来的**，
根本不走本地化表。结果同一局游戏里两套风格并存：

```
原版「消耗」    →  伊格佐斯特
生成卡「消耗」  →  消耗
```

本补丁就是补这一段。

---

## 它怎么做的

只做一件事：把东尼算法输出的**英文文本**，用音译尖塔的风格逐词音译。

```
Deal 6 damage.   →  迪尔·6·达米吉.
Gain 5 Block.    →  盖恩·5·布拉克.
Play a random Attack from your draw pile.
                 →  普雷·俄·兰德姆·阿泰克·弗拉姆·约尔·德罗·派尔.
```

关键在于**不猜东尼算法怎么分词**。它将来怎么改拆分方式、
怎么改名、怎么改模板都不影响——英文还是英文。

---

## 译表从哪来

运行时直接读音译尖塔自己的 `pck`：
同一键的 `eng` 表 × `zhs` 表逐 token 对齐，得到「英文 → 音译」词典。

| 项目 | 数量 |
|---|---|
| 对齐成功的表 | 46 / 46 |
| 英文词 | 3469 条 |
| 2~3 词短语 | 10243 条 |
| 完整卡名 | 820 条 |

- **不内嵌**任何词典
- **不派生**、**不打包**音译尖塔的数据
- 它更新译法，本补丁自动跟随

本 MOD 的 DLL 约 32 KB，里面只有代码。

---

## 五级词解析

保证不残留英文，同时尽量保持音译尖塔的用词：

| 级别 | 方法 | 示例 |
|---|---|---|
| 1 | 整词直查 | `Abrasive` → 阿布雷西夫 |
| 2 | 连写词切分 | `JackpotBlast` → 贾克帕特·布莱斯特 |
| 3 | 卡名子串切片 | `Clap` ← `Thunderclap` → 克莱普 |
| 4 | 前缀推断 | `Hemo` ← `Hemokinesis` → 希默 |
| 5 | 音节兜底 | 任何英文都能拼出可读音译 |

实测 400 段文本中，残留英文 **0** 段。

---

## 语法安全

SmartFormat 占位符与 BBCode 标签按**括号配对整块透传**，嵌套也认：

```
{InCombat: / (Deal {CalculatedDamage1:diff()} damage)|}   →  原样保留
[gold]Block[/gold]  →  [gold]布拉克[/gold]
```

音译结果会过滤掉 `< > [ ]` 等会被游戏当作标记解析的字符，
不会破坏格式串，也不会出现裸占位符。

---

## 覆盖范围

| 内容 | 是否处理 |
|---|---|
| 生成卡牌描述 | ✅ |
| 生成卡牌卡名 | ✅ |
| 混沌能力 / 状态效果描述 | ✅ |
| 东尼算法的设置项文本 | ✅ |
| 卡名图鉴、牌组界面 | ✅ |
| 原版卡牌与遗物 | 由音译尖塔负责，本补丁不干预 |

---

## 依赖

必须**同时安装并启用**：

- **Auto-Anthonyology**（东尼算法）
- **音译尖塔**（TransliterateTheSpire）

任一缺失时，本补丁**静默待机**：不报错、不写日志噪音、不影响游戏。

---

## 说明

- 无配置文件，无设置项，装上即生效
- 不改变任何游戏数值（`affects_gameplay = false`）
- 只处理东尼算法自己的内容，不触碰原版条目
- 已存在的旧存档中，卡名在生成时已写入存档数据，**不会回溯更新**；
  新开的局与图鉴显示正常

---

## English

A compatibility patch that makes **Auto-Anthonyology**'s generated card text
follow **TransliterateTheSpire**'s phonetic style.

TransliterateTheSpire works by overriding the game's localization tables.
Auto-Anthonyology, however, assembles its card descriptions, names and
chaos-power text at runtime — bypassing those tables entirely — so the two
mods end up with mixed styles in the same run.

This patch routes that text through TransliterateTheSpire's own translation
tables, which it reads from the mod's PCK at runtime (English tables aligned
against phonetic tables, key by key). No embedded dictionaries, no derived
data, no configuration. The DLL is ~32 KB of code only.

Word resolution is five-level (exact → compound split → title substring →
prefix inference → syllable fallback), and SmartFormat placeholders and
BBCode tags are passed through atomically, so format strings are never broken.

Requires both **Auto-Anthonyology** and **TransliterateTheSpire**.
Idles silently if either is missing.

---

## 致谢

- 音译尖塔 —— **AlkaliM**（译法与译表来源）
- 东尼算法 —— **Alriph**（本补丁服务的目标）
