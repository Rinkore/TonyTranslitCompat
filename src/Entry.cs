using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace TonyTranslitCompat;

// 东尼算法 × 音译尖塔 —— 兼容补丁。
//
// 【问题】东尼算法在运行时自己拼文本（卡牌描述 / 卡名 / 状态效果 / 静态键），
//         绕过了 LocManager 的本地化表，音译尖塔的 GetTable 覆盖碰不到它们。
//
// 【思路】音译尖塔的译法就是「英文原文的逐词音译」。
//         所以本补丁不猜东尼算法怎么分词，也不读它的内部结构，
//         只做一件事：把它输出的英文文本，用音译尖塔自己学出来的词典逐词音译。
//         东尼算法将来怎么改拆分方式都不影响——英文还是英文。
//
// 【数据】全部在运行时从音译尖塔的 pck 读取（eng 表 × zhs 表按同键对齐），
//         不内嵌、不派生、不打包它的任何数据。它更新译法，本补丁自动跟随。
//
// 【安全】{...} 与 [...] 按括号配对整块透传，绝不改写语法；
//         音译结果过滤掉 < > [ ] 等会被当成标记的字符。
[ModInitializer("Init")]
public static class Entry
{
    private const string AutoAnthony = "AutoAnthony";
    private const string Transliterate = "TransliterateTheSpire";

    public static void Init()
    {
        try
        {
            if (!Enabled(AutoAnthony))
            {
                Log.Info("[东尼音译兼容] 未启用东尼算法，待机。", 2);
                return;
            }
            if (!Enabled(Transliterate))
            {
                Log.Info("[东尼音译兼容] 未启用音译尖塔，待机。", 2);
                return;
            }

            var sw = Stopwatch.StartNew();
            if (!Lexicon.Load())
            {
                Log.Error("[东尼音译兼容] 词表构建失败，补丁待机。");
                return;
            }
            Patches.Apply();
            sw.Stop();
            Log.Info($"[东尼音译兼容] 已启用：词 {Lexicon.WordCount} / 短语 {Lexicon.PhraseCount} / 卡名 {Lexicon.TitleCount}，耗时 {sw.ElapsedMilliseconds} ms。", 2);
        }
        catch (Exception e)
        {
            Log.Error("[东尼音译兼容] 初始化失败，东尼算法文本保持原样：" + System.Environment.NewLine + e);
        }
    }

    private static bool Enabled(string id)
    {
        try
        {
            // 加载阶段被禁用的 mod 已从 ModManager.Mods 移除，这里的都是已启用。
            return ModManager.Mods.Any(m => m.manifest != null && m.manifest.id == id);
        }
        catch
        {
            return false;
        }
    }
}

// ===================== 音译引擎 =====================

// 三张表全部来自「音译尖塔 pck 里同键的 eng 表 × zhs 表」：
//   Words   —— 英文词 → 音译
//   Phrases —— 2~3 词短语 → 音译（处理语序）
//   Titles  —— 完整卡名 → 音译（用于把词根碎片切出来）
internal static class Lexicon
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Phrases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static int _leakLogged;

    // 东尼算法自己的 eng 表：静态键 → 英文原文
    private static readonly Dictionary<string, string> AaEnglish = new(StringComparer.Ordinal);
    // 生成卡名：中文名 → 英文名（AA 先写中文标题、后生成英文名，靠它补写）
    private static readonly ConcurrentDictionary<string, string> NameZhToEn = new(StringComparer.Ordinal);

    public static int WordCount => Words.Count;
    public static int PhraseCount => Phrases.Count;
    public static int TitleCount => Titles.Count;

    // ---------------------------------------------------------------- 构建

    public static bool Load()
    {
        string ttsDir = FindModDir("TransliterateTheSpire");
        if (ttsDir == null)
        {
            Log.Error("[东尼音译兼容] 找不到音译尖塔目录。");
            return false;
        }

        string pck = Path.Combine(ttsDir, "TransliterateTheSpire.pck");
        var eng = ReadTables(pck, "/eng/");
        var zhs = ReadTables(pck, "/zhs/");
        if (eng.Count == 0 || zhs.Count == 0)
        {
            Log.Error("[东尼音译兼容] 音译尖塔 pck 里没有 eng/zhs 表。");
            return false;
        }

        int pairs = Align(eng, zhs);
        LoadAaEnglish();
        Prune();
        Log.Info($"[东尼音译兼容] 词表来源：{ttsDir}（{eng.Count}/{zhs.Count} 张表，对齐 {pairs} 条）", 2);
        return Words.Count > 0;
    }

    /// <summary>同键逐 token 对齐，词数一致才采信，避免错位。</summary>
    private static int Align(Dictionary<string, Dictionary<string, string>> eng,
                             Dictionary<string, Dictionary<string, string>> zhs)
    {
        int pairs = 0;
        foreach (var (table, etab) in eng)
        {
            if (!zhs.TryGetValue(table, out var ztab)) continue;

            foreach (var (key, sentence) in etab)
            {
                if (!ztab.TryGetValue(key, out string translated)) continue;
                if (string.IsNullOrWhiteSpace(sentence) || string.IsNullOrWhiteSpace(translated)) continue;

                var et = Scan(sentence).Where(t => t.Kind == Kind.Word).Select(t => t.Text).ToList();
                var zt = ScanPhonetic(translated);
                if (et.Count == 0 || et.Count != zt.Count) continue;
                pairs++;

                for (int i = 0; i < et.Count; i++)
                    Words.TryAdd(et[i], zt[i]);

                for (int n = 2; n <= 3; n++)
                    for (int i = 0; i + n <= et.Count; i++)
                        Phrases.TryAdd(string.Join(" ", et.Skip(i).Take(n)), string.Join("·", zt.Skip(i).Take(n)));

                if (key.EndsWith(".title", StringComparison.Ordinal) && et.Count == 1)
                    Titles.TryAdd(et[0], zt[0]);
            }
        }
        return pairs;
    }

    /// <summary>东尼算法自己的英文表（静态键用）。</summary>
    private static void LoadAaEnglish()
    {
        try
        {
            string dir = FindModDir("AutoAnthony");
            if (dir == null) return;
            string pck = Path.Combine(dir, "AutoAnthony.pck");
            if (!File.Exists(pck)) return;

            foreach (var (_, table) in ReadTables(pck, "/eng/"))
                foreach (var (key, value) in table)
                    if (!string.IsNullOrWhiteSpace(value))
                        AaEnglish.TryAdd(key, value);

            Log.Info($"[东尼音译兼容] 东尼算法英文表：{AaEnglish.Count} 条。", 2);
        }
        catch (Exception e)
        {
            Log.Error("[东尼音译兼容] 读取东尼算法英文表失败：" + e.Message);
        }
    }

    /// <summary>清掉带语法字符的脏条目（音译里不该出现 &lt; &gt; [ ]）。</summary>
    private static void Prune()
    {
        int removed = 0;
        foreach (var dict in new[] { Words, Phrases, Titles })
        {
            foreach (string key in dict.Where(kv => HasSyntax(kv.Value)).Select(kv => kv.Key).ToList())
            {
                dict.Remove(key);
                removed++;
            }
        }
        if (removed > 0)
            Log.Info($"[东尼音译兼容] 清理了 {removed} 条含语法字符的词条。", 2);
    }

    // ---------------------------------------------------------------- 查询

    public static string EnglishForKey(string key) =>
        AaEnglish.TryGetValue(key, out string value) ? value : null;

    public static void RememberName(string chinese, string english)
    {
        if (!string.IsNullOrWhiteSpace(chinese) && !string.IsNullOrWhiteSpace(english))
            NameZhToEn[chinese] = english;
    }

    public static int NameMapCount => NameZhToEn.Count;

    public static string EnglishForName(string chinese) =>
        chinese != null && NameZhToEn.TryGetValue(chinese, out string english) ? english : null;

    // ---------------------------------------------------------------- 翻译

    /// <summary>把一段英文音译成音译尖塔风格。已是中文/已音译的文本原样返回。</summary>
    public static string Translate(string english)
    {
        if (string.IsNullOrEmpty(english)) return english;
        if (!HasAsciiLetter(english)) return english;      // 幂等：纯中文不重复处理
        if (Cache.TryGetValue(english, out string cached)) return cached;

        string result = TranslateCore(english);
        if (Cache.Count < 20000) Cache.TryAdd(english, result);

        return result;
    }

    private static string TranslateCore(string english)
    {
        var tokens = Scan(english);
        var output = new StringBuilder(english.Length * 2);
        bool lastWasContent = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            switch (token.Kind)
            {
                case Kind.Syntax:
                    if (IsClosingTag(token.Text))
                    {
                        output.Append(token.Text);          // [/gold] 直接接在后面
                        lastWasContent = true;
                    }
                    else
                    {
                        if (lastWasContent) output.Append('·');
                        output.Append(token.Text[0] == '{' ? SmartBlock(token.Text) : token.Text);
                        lastWasContent = token.Text[0] == '{';   // 占位符算内容，[标签] 之后紧跟词不加 ·
                    }
                    break;

                case Kind.Number:
                    if (lastWasContent) output.Append('·');
                    output.Append(token.Text);
                    lastWasContent = true;
                    break;

                case Kind.Word:
                    string translated = TranslateWord(tokens, ref i);
                    if (lastWasContent) output.Append('·');
                    output.Append(translated);
                    lastWasContent = true;
                    break;

                default:
                    if (!char.IsWhiteSpace(token.Text[0]))        // 空白由 · 表达
                    {
                        output.Append(token.Text);
                        lastWasContent = false;
                    }
                    break;
            }
        }
        return output.ToString();
    }

    /// <summary>
    /// SmartFormat 块：{Name:...}。
    /// 纯格式器（diff()、energyIcons()）原样透传；
    /// 含显示文本的分支（带空格或 |）只音译文本部分，骨架不动。
    /// </summary>
    private static string SmartBlock(string block)
    {
        if (block.Length < 3 || block[0] != '{' || block[^1] != '}') return block;
        string content = block.Substring(1, block.Length - 2);

        int colon = IndexTopLevel(content, ':');
        if (colon < 0) return block;                       // {Variable}

        string head = content.Substring(0, colon);
        string rest = content.Substring(colon + 1);

        // 没有显示文本（diff() 这类）就透传
        if (rest.IndexOf(' ') < 0 && rest.IndexOf('|') < 0) return block;

        string prefix = "";
        string body = rest;
        foreach (string keyword in new[] { "show:", "hide:", "plural:" })
        {
            if (!body.StartsWith(keyword, StringComparison.Ordinal)) continue;
            prefix = keyword;
            body = body.Substring(keyword.Length);
            break;
        }

        var pieces = SplitTopLevel(body, '|');
        var rebuilt = new StringBuilder();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (i > 0) rebuilt.Append('|');
            rebuilt.Append(TranslateCore(pieces[i]));
        }
        return "{" + head + ":" + prefix + rebuilt + "}";
    }

    private static int IndexTopLevel(string s, char target)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{' || c == '(') depth++;
            else if (c == '}' || c == ')') depth--;
            else if (c == target && depth == 0) return i;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{' || c == '(') depth++;
            else if (c == '}' || c == ')') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts;
    }

    private static bool IsClosingTag(string syntax) =>
        syntax.StartsWith("[/", StringComparison.Ordinal) || syntax == "]";

    /// <summary>翻译一个词（可能是多词短语），成功时把 i 推进到短语末尾。</summary>
    private static string TranslateWord(List<Token> tokens, ref int i)
    {
        // 1) 短语优先：最长 3 词
        for (int n = 3; n >= 2; n--)
        {
            if (i + n > tokens.Count) continue;
            var slice = tokens.Skip(i).Take(n).ToList();
            if (slice.Any(t => t.Kind != Kind.Word)) continue;
            if (Phrases.TryGetValue(string.Join(" ", slice.Select(t => t.Text)), out string phrase))
            {
                i += n - 1;
                return phrase;
            }
        }

        string word = tokens[i].Text;

        // 2) 整词
        if (Words.TryGetValue(word, out string hit)) return hit;

        // 3) 连写词：驼峰切分（JackpotBlast）或词典贪心切分（CompressNeedle）
        if (SplitCompound(word) is string compound) return compound;

        // 4) 词根碎片：从完整卡名里按位置比例切（Clap ← Thunderclap）
        if (SliceFromTitle(word) is string sliced) return sliced;

        // 5) 前缀推断：被截断的词（Im ← Imaginary、Hemo ← Hemokinesis）
        if (word.Length >= 3 && PrefixMatch(word) is string prefixed) return prefixed;

        // 6) 音节兜底：任何英文都能拼出音译，保证不残留英文
        string syllable = Syllable(word);
        if (syllable == null && _leakLogged < 10)
        {
            _leakLogged++;
            Log.Info($"[东尼音译兼容] 漏译词: {word}", 2);
        }
        return syllable ?? word;
    }

    /// <summary>被截断的词根：找以它开头（或它包含）的已知词，按比例截取音译。</summary>
    private static string PrefixMatch(string word)
    {
        if (word.Length < 3) return null;

        string bestKey = null, bestValue = null;
        int bestScore = int.MaxValue;

        foreach (var (key, value) in Words)
        {
            if (key.Length < 3) continue;

            int score;
            if (key.StartsWith(word, StringComparison.OrdinalIgnoreCase) && key.Length > word.Length)
                score = key.Length - word.Length;                 // 词是已知词的前缀
            else if (word.StartsWith(key, StringComparison.OrdinalIgnoreCase) && word.Length > key.Length)
                score = 1000 + word.Length - key.Length;          // 已知词是词的前缀
            else continue;

            if (score >= bestScore) continue;
            bestScore = score;
            bestKey = key;
            bestValue = value;
        }
        if (bestKey == null) return null;

        if (bestKey.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            int cut = Math.Max(1, (int)Math.Round(bestValue.Length * (double)word.Length / bestKey.Length));
            return bestValue.Substring(0, Math.Min(cut, bestValue.Length));
        }
        return bestValue;
    }

    /// <summary>音节级兜底：任何英文词都能拼出可读的音译。</summary>
    private static string Syllable(string word)
    {
        string text = new string(word.ToLowerInvariant().Where(char.IsAsciiLetter).ToArray());
        if (text.Length == 0) return null;

        // 静音词尾 e
        if (text.Length > 3 && text.EndsWith("e", StringComparison.Ordinal) && text.Any(c => "aeiouy".Contains(c)))
            text = text.Substring(0, text.Length - 1);

        var output = new StringBuilder();
        int i = 0, guard = 0;
        while (i < text.Length && guard++ < 40)
        {
            string onset = TakePrefix(text, ref i, Onsets);
            string vowel = TakePrefix(text, ref i, Vowels);
            if (vowel == null)
            {
                if (onset != null) { output.Append(onset); continue; }
                i++;
                continue;
            }
            string coda = TakePrefix(text, ref i, Codas);
            if (coda != null && i < text.Length && "aeiouy".Contains(text[i]))
                output.Append(coda);                               // 韵尾其实属于下一音节
            output.Append(onset).Append(vowel).Append(coda ?? "");
        }
        return output.Length > 0 ? output.ToString() : null;
    }

    private static string TakePrefix(string text, ref int index, (string Pattern, string Output)[] table)
    {
        foreach (var (pattern, output) in table)
        {
            if (!text.AsSpan(index).StartsWith(pattern, StringComparison.Ordinal)) continue;
            index += pattern.Length;
            return output;
        }
        return null;
    }

    private static readonly (string, string)[] Onsets =
    {
        ("sch","什"),("shr","什尔"),("str","斯特"),("spr","斯普"),("thr","斯尔"),
        ("sh","什"),("ch","奇"),("th","斯"),("ph","弗"),("wh","沃"),("ck","克"),
        ("bl","布尔"),("br","布尔"),("cl","克尔"),("cr","克尔"),("dr","德尔"),
        ("fl","弗尔"),("fr","弗尔"),("gl","格尔"),("gr","格尔"),("pl","普尔"),
        ("pr","普尔"),("sl","斯尔"),("sp","斯普"),("st","斯特"),("sc","斯克"),
        ("b","布"),("c","克"),("d","德"),("f","弗"),("g","格"),("h","赫"),
        ("j","杰"),("k","克"),("l","尔"),("m","姆"),("n","恩"),("p","普"),
        ("q","克"),("r","尔"),("s","斯"),("t","特"),("v","夫"),("w","沃"),
        ("x","克斯"),("z","兹"),
    };

    private static readonly (string, string)[] Vowels =
    {
        ("eigh","艾"),("igh","艾"),("ai","艾"),("ay","艾"),("ea","伊"),("ee","伊"),
        ("ei","埃"),("ey","埃"),("ie","艾"),("oa","奥"),("oo","乌"),("ou","奥"),
        ("ow","奥"),("oi","奥伊"),("oy","奥伊"),("au","奥"),("aw","奥"),("ue","尤"),
        ("a","阿"),("e","埃"),("i","伊"),("o","奥"),("u","乌"),("y","伊"),
    };

    private static readonly (string, string)[] Codas =
    {
        ("ng","恩"),("nk","恩克"),("ck","克"),("sh","什"),("ch","奇"),("th","斯"),
        ("ss","斯"),("ll","尔"),("tt","特"),("pp","普"),("b","布"),("d","德"),
        ("f","夫"),("g","格"),("k","克"),("l","尔"),("m","姆"),("n","恩"),
        ("p","普"),("r","尔"),("s","斯"),("t","特"),("x","克斯"),("z","兹"),
    };

    /// <summary>把连写词拆成已知词：先驼峰，再词典贪心最长匹配。</summary>
    private static string SplitCompound(string word)
    {
        var parts = SplitCamel(word);
        if (parts.Count > 1 && parts.All(p => Words.ContainsKey(p)))
            return string.Join("·", parts.Select(p => Words[p]));

        var segments = new List<string>();
        int i = 0;
        while (i < word.Length)
        {
            string hit = null;
            for (int length = word.Length - i; length >= 3; length--)
            {
                if (Words.TryGetValue(word.Substring(i, length), out string found))
                {
                    hit = found;
                    i += length;
                    break;
                }
            }
            if (hit == null) return null;
            segments.Add(hit);
        }
        return segments.Count > 0 ? string.Join("·", segments) : null;
    }

    /// <summary>在已知卡名里找该词，按位置比例切出对应音译。</summary>
    private static string SliceFromTitle(string word)
    {
        if (word.Length < 2) return null;

        string needle = word.ToLowerInvariant();
        string bestFull = null, bestPhonetic = null;
        int bestIndex = 0, bestScore = int.MaxValue;

        foreach (var (full, phonetic) in Titles)
        {
            int index = full.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            bool wholeWord = (index == 0 || !char.IsLetter(full[index - 1]))
                          && (index + needle.Length == full.Length || !char.IsLetter(full[index + needle.Length]));

            int score = (wholeWord ? 0 : 1000) + Math.Abs(full.Length - needle.Length);
            if (score >= bestScore) continue;

            bestScore = score;
            bestFull = full;
            bestPhonetic = phonetic;
            bestIndex = index;
        }
        if (bestFull == null) return null;

        int start = (int)Math.Round(bestPhonetic.Length * (double)bestIndex / bestFull.Length);
        int end = (int)Math.Round(bestPhonetic.Length * (double)(bestIndex + needle.Length) / bestFull.Length);
        start = Math.Clamp(start, 0, bestPhonetic.Length);
        end = Math.Clamp(end, start, bestPhonetic.Length);

        string slice = TrimEdges(bestPhonetic.Substring(start, end - start));
        return slice.Length > 0 ? slice : null;
    }

    // ---------------------------------------------------------------- 分词

    private enum Kind { Word, Number, Syntax, Other }

    private readonly record struct Token(Kind Kind, string Text);

    /// <summary>
    /// 单遍扫描：{...} 与 [...] 按配对整块识别（支持嵌套），
    /// 其余按 ASCII 单词 / 数字 / 标点切分。不做正则匹配。
    /// </summary>
    private static List<Token> Scan(string text)
    {
        var tokens = new List<Token>(text.Length / 4 + 8);
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '{' && MatchPair(text, i, '{', '}') is int braceEnd)
            {
                tokens.Add(new Token(Kind.Syntax, text.Substring(i, braceEnd - i + 1)));
                i = braceEnd + 1;
                continue;
            }

            if (c == '[' && MatchPair(text, i, '[', ']') is int bracketEnd)
            {
                tokens.Add(new Token(Kind.Syntax, text.Substring(i, bracketEnd - i + 1)));
                i = bracketEnd + 1;
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))) i++;
                tokens.Add(new Token(Kind.Number, text.Substring(start, i - start)));
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiLetter(text[i]) || text[i] == '\'' || text[i] == '’' || text[i] == '-')) i++;
                tokens.Add(new Token(Kind.Word, text.Substring(start, i - start)));
                continue;
            }

            tokens.Add(new Token(Kind.Other, c.ToString()));
            i++;
        }
        return tokens;
    }

    /// <summary>返回与 open 配对的闭合括号下标；不配对返回 null。</summary>
    private static int? MatchPair(string text, int open, char opening, char closing)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == opening) depth++;
            else if (text[i] == closing && --depth == 0) return i;
        }
        return null;
    }

    /// <summary>音译文本按 · 与换行切成纯词条，两端标点一律剥掉。</summary>
    private static List<string> ScanPhonetic(string text)
    {
        var words = new List<string>();
        var buffer = new StringBuilder();

        void Flush()
        {
            foreach (string segment in buffer.ToString().Split('·'))
            {
                string word = TrimEdges(segment);
                if (word.Length > 0) words.Add(word);
            }
            buffer.Clear();
        }

        foreach (char c in text)
        {
            if (c == '{' || c == '[' || c == '}' || c == ']' || c == '\n') Flush();
            else buffer.Append(c);
        }
        Flush();
        return words;
    }

    private static readonly char[] EdgePunctuation =
        "<>[](){}「」『』.,;:!?，。、；：！？…·-–—/\\|\"'“”‘’《》〈〉".ToCharArray();

    private static readonly char[] EdgeTrimChars =
        "<>[](){}「」『』.,;:!?，。、；：！？…·-–—/\\|\"'“”‘’《》〈〉 \t\r\n".ToCharArray();

    private static string TrimEdges(string s) => s.Trim(EdgeTrimChars);

    private static bool HasSyntax(string s)
    {
        foreach (char c in s)
            if (c == '<' || c == '>' || c == '[' || c == ']') return true;
        return false;
    }

    internal static bool HasAsciiLetter(string s)
    {
        foreach (char c in s)
            if (char.IsAsciiLetter(c)) return true;
        return false;
    }

    private static List<string> SplitCamel(string word) =>
        word.Split(new[] { '-', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => SplitUpper(part))
            .Where(p => p.Length > 0)
            .ToList();

    private static IEnumerable<string> SplitUpper(string word)
    {
        var buffer = new StringBuilder();
        foreach (char c in word)
        {
            if (char.IsUpper(c) && buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }
            buffer.Append(c);
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }

    // ---------------------------------------------------------------- 路径与 PCK

    private static string FindModDir(string id)
    {
        try
        {
            foreach (var mod in ModManager.Mods)
            {
                if (mod.manifest == null || mod.manifest.id != id) continue;
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == id);
                if (asm != null && !string.IsNullOrEmpty(asm.Location))
                    return Path.GetDirectoryName(asm.Location);
            }
        }
        catch { }

        try
        {
            string dir = Path.GetDirectoryName(typeof(Lexicon).Assembly.Location);
            for (int up = 0; up < 10 && dir != null; up++)
            {
                string workshop = Path.Combine(dir, "steamapps", "workshop", "content", "2868840");
                if (Directory.Exists(workshop))
                {
                    foreach (string sub in Directory.GetDirectories(workshop))
                        if (File.Exists(Path.Combine(sub, id + ".json"))) return sub;
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    /// <summary>读 Godot PCK v3 里指定语言目录下的所有 json 表。</summary>
    private static Dictionary<string, Dictionary<string, string>> ReadTables(string pckPath, string languagePath)
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!File.Exists(pckPath)) return tables;

        try
        {
            using var stream = File.OpenRead(pckPath);
            using var reader = new BinaryReader(stream);

            if (new string(reader.ReadChars(4)) != "GDPC") return tables;

            reader.BaseStream.Position = 4;
            reader.ReadUInt32();                                   // pack format
            reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32();  // godot version
            reader.ReadUInt32();                                   // flags
            ulong fileBase = reader.ReadUInt64();
            ulong directoryOffset = reader.ReadUInt64();

            reader.BaseStream.Position = (long)directoryOffset;
            uint count = reader.ReadUInt32();

            // 先把目录全部读出来，再去读内容——边遍历边跳转会破坏目录游标。
            var entries = new List<(string Name, ulong Offset, ulong Size, uint Flags)>((int)count);
            for (uint n = 0; n < count; n++)
            {
                int nameLength = (int)reader.ReadUInt32();
                string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength)).TrimEnd('\0');
                ulong offset = reader.ReadUInt64();
                ulong size = reader.ReadUInt64();
                reader.ReadBytes(16);                              // md5
                uint flags = reader.ReadUInt32();
                entries.Add((name, offset, size, flags));
            }

            foreach (var entry in entries)
            {
                if (!entry.Name.Contains(languagePath, StringComparison.Ordinal)) continue;
                if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if ((entry.Flags & 1) != 0) continue;              // 加密条目跳过

                reader.BaseStream.Position = (long)(fileBase + entry.Offset);
                var table = ParseJson(reader.ReadBytes((int)entry.Size));
                if (table != null && table.Count > 0)
                    tables[Path.GetFileNameWithoutExtension(entry.Name)] = table;
            }
        }
        catch (Exception e)
        {
            Log.Error($"[东尼音译兼容] 读取 pck 失败（{languagePath}）：{e.Message}");
        }
        return tables;
    }

    private static Dictionary<string, string> ParseJson(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes); }
        catch
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes.AsSpan(3)); }  // 跳过 BOM
            catch { return null; }
        }
    }
}

// ===================== Harmony 钩子 =====================

internal static class Patches
{
    private static bool IsChinese
    {
        get
        {
            var manager = LocManager.Instance;
            return manager != null && (manager.Language == "zhs" || manager.Language == "zht");
        }
    }

    public static void Apply()
    {
        var harmony = new Harmony("sts2.compat.tonytranslit");

        // 1) 卡牌描述：强制走英文渲染，再音译
        Patch(harmony, "AutoAnthony.ChaosRuntimeDescriptionCache:GetFormatted", "卡牌描述",
            prefix: nameof(ForceEnglish), postfix: nameof(TranslateResult));

        // 2) 卡牌卡名：DynamicTitle 返回中文名，需先换成英文再音译
        Patch(harmony, "AutoAnthony.ChaosCardModel:get_DynamicTitle", "卡牌卡名",
            postfix: nameof(TranslateTitle));

        // 3) 状态效果描述
        Patch(harmony, "AutoAnthony.ChaosCompositePower:GetDescriptionText", "状态效果",
            postfix: nameof(TranslateResult));

        // 4) 卡名生成时记录 中文→英文，供本地化表补写
        Patch(harmony, "ChaosCardGenerator.CardNameGenerator:Generate", "卡名记录",
            postfix: nameof(RememberName));

        // 5) 装标题时临时切成英文，让 AA 自己给出英文名（读档场景也适用）
        Patch(harmony, "AutoAnthony.ChaosRunDefinitions:InstallLocalizedTitles", "标题安装语言",
            prefix: nameof(TitlesToEnglish), finalizer: nameof(RestoreLanguage));

        // 6) 东尼算法把标题写进本地化表时音译
        Patch(harmony, "AutoAnthony.ChaosRuntimeDescriptionCache:InstallAllIfChanged", "标题安装",
            prefix: nameof(TranslateTitles));
        Patch(harmony, "AutoAnthony.ChaosRuntimeDescriptionCache:InstallIfChanged", "单条安装",
            prefix: nameof(TranslateSingle));

        // 7) 读取本地化表时补译东尼算法的静态键
        harmony.Patch(AccessTools.Method(typeof(LocManager), "GetTable"),
            postfix: new HarmonyMethod(typeof(Patches), nameof(TranslateTable)));
    }

    private static void Patch(Harmony harmony, string target, string label,
                              string prefix = null, string postfix = null, string finalizer = null)
    {
        var method = AccessTools.Method(target);
        if (method == null)
        {
            Log.Error($"[东尼音译兼容] 未找到 {label} 钩子（{target}）。");
            return;
        }
        harmony.Patch(method,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix),
            finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Patches), finalizer));
    }

    private static int _titleCalls, _titleHits;

    // ---- 卡名：直接从卡实例读它自己的英文名，再音译 ----
    private static void TranslateTitle(CardModel __instance, ref string __result)
    {
        _titleCalls++;
        if (!IsChinese || string.IsNullOrEmpty(__result) || __instance == null) return;

        string suffix = "";
        string name = __result;
        if (name.EndsWith("+", StringComparison.Ordinal))
        {
            suffix = "+";
            name = name.Substring(0, name.Length - 1);
        }

        string english = ReadEnglishName(__instance) ?? Lexicon.EnglishForName(name);
        if (string.IsNullOrEmpty(english)) return;

        string translated = Lexicon.Translate(english);
        if (string.IsNullOrEmpty(translated) || translated == name) return;

        _titleHits++;
        if (_titleHits <= 6)
            Log.Info($"[东尼音译兼容] 卡名 {name} → {english} → {translated}", 2);

        __result = translated + suffix;
    }

    private static readonly Dictionary<Type, PropertyInfo> GeneratedCache = new();

    private static string ReadEnglishName(object card)
    {
        try
        {
            var type = card.GetType();
            if (!GeneratedCache.TryGetValue(type, out var generatedProp))
            {
                generatedProp = AccessTools.Property(type, "Generated");
                GeneratedCache[type] = generatedProp;
            }
            object generated = generatedProp?.GetValue(card);
            if (generated == null) return null;

            object name = AccessTools.Property(generated.GetType(), "Name")?.GetValue(generated);
            return AccessTools.Property(name?.GetType(), "English")?.GetValue(name) as string;
        }
        catch { return null; }
    }

    // ---- 装标题前临时切英文 ----
    // AA 的 InstallLocalizedTitles 按当前语言选中文名或英文名。
    // 切成 eng 后它装的就是英文标题，TranslateTitles 再音译，
    // 读档场景（没有 Generate 记录）也能正确工作。
    private static readonly FieldInfo LanguageField =
        AccessTools.Field(typeof(LocManager), "<Language>k__BackingField");

    private static void TitlesToEnglish(ref string __state)
    {
        __state = null;
        if (!IsChinese) return;
        try
        {
            var manager = LocManager.Instance;
            if (manager == null) return;
            if (LanguageField == null)
            {
                Log.Error("[东尼音译兼容] 找不到 LocManager 的语言字段，标题将保持中文。");
                return;
            }
            __state = manager.Language;
            LanguageField.SetValue(manager, "eng");
        }
        catch (Exception e)
        {
            Log.Error("[东尼音译兼容] 切换标题语言失败：" + e.Message);
        }
    }

    private static void RestoreLanguage(string __state, Exception __exception)
    {
        if (__state == null) return;
        try
        {
            LanguageField?.SetValue(LocManager.Instance, __state);
        }
        catch (Exception e)
        {
            Log.Error("[东尼音译兼容] 恢复语言失败：" + e.Message);
        }
    }

    // ---- 通用后置：译文一律走同一套音译 ----
    private static void TranslateResult(ref string __result)
    {
        if (IsChinese && !string.IsNullOrEmpty(__result))
            __result = Lexicon.Translate(__result);
    }

    // ---- GetFormatted(card, chinese)：强制英文，让音译有源文本 ----
    private static void ForceEnglish(ref bool chinese)
    {
        if (IsChinese) chinese = false;
    }

    // ---- CardNameGenerator.Generate：记录中文名 → 英文名 ----
    private static void RememberName(object __result)
    {
        if (__result == null) return;
        try
        {
            var type = __result.GetType();
            string chinese = AccessTools.Property(type, "Chinese")?.GetValue(__result) as string;
            string english = AccessTools.Property(type, "English")?.GetValue(__result) as string;
            if (string.IsNullOrWhiteSpace(english)) return;

            Lexicon.RememberName(chinese, english);
            if (string.IsNullOrWhiteSpace(chinese)) return;

            // 标题可能已被 AA 提前写进表里，这里回头补写
            string translated = Lexicon.Translate(english);
            if (!string.IsNullOrEmpty(translated))
                RewriteTitles(chinese, translated);
        }
        catch (Exception e)
        {
            Log.Error("[东尼音译兼容] 记录卡名失败：" + e.Message);
        }
    }

    // ---- InstallAllIfChanged(table, templates)：把 .title 换成音译 ----
    private static void TranslateTitles(ref object templates)
    {
        if (!IsChinese || templates is not IReadOnlyDictionary<string, string> source) return;

        var copy = new Dictionary<string, string>(source, StringComparer.Ordinal);
        bool changed = false;

        foreach (var (key, chinese) in source)
        {
            if (!key.EndsWith(".title", StringComparison.Ordinal)) continue;

            // 已是英文（语言翻转后的正常情况）就直接音译；中文则查映射（兜底）
            string englishSource = Lexicon.HasAsciiLetter(chinese) ? chinese : Lexicon.EnglishForName(chinese);
            if (englishSource == null) continue;

            string translated = Lexicon.Translate(englishSource);
            if (string.IsNullOrEmpty(translated) || translated == chinese) continue;

            copy[key] = translated;
            changed = true;
        }
        if (changed) templates = copy;
    }

    // ---- InstallIfChanged(table, key, template) ----
    private static void TranslateSingle(string key, ref string template)
    {
        if (!IsChinese || string.IsNullOrEmpty(template)) return;
        if (key.EndsWith(".description", StringComparison.Ordinal))
            template = Lexicon.Translate(template);
    }

    // ---- LocManager.GetTable：补译东尼算法静态键 ----
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LocTable, object> Visited = new();

    private static void TranslateTable(string __0, LocTable __result)
    {
        if (!IsChinese || __result == null) return;
        if (Visited.TryGetValue(__result, out _)) return;
        Visited.GetValue(__result, _ => new object());

        try
        {
            if (AccessTools.Field(typeof(LocTable), "_translations")?.GetValue(__result)
                is not IDictionary<string, string> entries) return;

            var updates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, chinese) in entries)
            {
                if (string.IsNullOrWhiteSpace(chinese)) continue;

                string english = Lexicon.EnglishForKey(key);
                if (english == null && key.EndsWith(".title", StringComparison.Ordinal))
                    english = Lexicon.EnglishForName(chinese);
                if (english == null) continue;

                string translated = Lexicon.Translate(english);
                if (!string.IsNullOrEmpty(translated) && translated != chinese)
                    updates[key] = translated;
            }

            if (updates.Count == 0) return;
            __result.MergeWith(updates);
            Log.Info($"[东尼音译兼容] 表 {__0} 补译 {updates.Count} 条。", 2);
        }
        catch (Exception e)
        {
            Log.Error("[东尼音译兼容] 本地化表补译失败：" + e.Message);
        }
    }

    // ---- 把表里所有值等于该中文名的 .title 键改成音译 ----
    private static void RewriteTitles(string chinese, string translated)
    {
        try
        {
            var table = LocManager.Instance?.GetTable("cards");
            if (table == null) return;
            if (AccessTools.Field(typeof(LocTable), "_translations")?.GetValue(table)
                is not IDictionary<string, string> entries) return;

            var updates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in entries)
                if (key.EndsWith(".title", StringComparison.Ordinal) && value == chinese)
                    updates[key] = translated;

            if (updates.Count > 0) table.MergeWith(updates);
        }
        catch (Exception e)
        {
            Log.Error("[东尼音译兼容] 补写标题失败：" + e.Message);
        }
    }
}
