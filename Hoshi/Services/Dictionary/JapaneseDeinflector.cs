using System;
using System.Collections.Generic;
using System.Linq;
using Hoshi.Models.Dictionary;

namespace Hoshi.Services.Dictionary;

[Flags]
public enum JapaneseDeinflectionConditions : uint
{
    None = 0,
    V1D = 1 << 0,
    V1P = 1 << 1,
    V5D = 1 << 2,
    V5SS = 1 << 3,
    V5SP = 1 << 4,
    VK = 1 << 5,
    VS = 1 << 6,
    VZ = 1 << 7,
    ADJ_I = 1 << 8,
    MASU = 1 << 9,
    MASEN = 1 << 10,
    TE = 1 << 11,
    BA = 1 << 12,
    KU = 1 << 13,
    TA = 1 << 14,
    NN = 1 << 15,
    NASAI = 1 << 16,
    YA = 1 << 17,

    // Composites
    V1 = V1D | V1P,
    V5S = V5SS | V5SP,
    V5 = V5D | V5S,
    V = V1 | V5 | VK | VS | VZ,
}

public sealed record JapaneseDeinflectionResult(
    string Text,
    JapaneseDeinflectionConditions Conditions,
    List<TransformGroup> Trace
);

public sealed class JapaneseDeinflector
{
    public static JapaneseDeinflector Instance { get; }

    private readonly Dictionary<string, List<Rule>> _transforms = new();
    private readonly List<TransformGroup> _groups = [];
    private int _maxSuffixLength;

    static JapaneseDeinflector()
    {
        Instance = new JapaneseDeinflector();
    }

    private JapaneseDeinflector()
    {
        InitTransforms();
    }

    public List<JapaneseDeinflectionResult> Deinflect(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= 1)
        {
            return [new JapaneseDeinflectionResult(text ?? "", JapaneseDeinflectionConditions.None, [])];
        }

        var results = new List<JapaneseDeinflectionResult>();
        DeinflectRecursive(text, JapaneseDeinflectionConditions.None, [], results);
        return results;
    }

    public static JapaneseDeinflectionConditions PosToConditions(string rules)
    {
        if (string.IsNullOrWhiteSpace(rules))
            return JapaneseDeinflectionConditions.None;

        var conditions = JapaneseDeinflectionConditions.None;
        foreach (var rule in rules.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            conditions |= rule switch
            {
                "v1" => JapaneseDeinflectionConditions.V1,
                "v5" => JapaneseDeinflectionConditions.V5,
                "vk" => JapaneseDeinflectionConditions.VK,
                "vs" or "vs-i" or "vs-s" => JapaneseDeinflectionConditions.VS,
                "vz" => JapaneseDeinflectionConditions.VZ,
                "adj-i" => JapaneseDeinflectionConditions.ADJ_I,
                _ => JapaneseDeinflectionConditions.None,
            };
        }

        return conditions;
    }

    private void DeinflectRecursive(
        string text,
        JapaneseDeinflectionConditions conditions,
        List<TransformGroup> trace,
        List<JapaneseDeinflectionResult> results)
    {
        if (text.Length <= 1) return;

        results.Add(new JapaneseDeinflectionResult(text, conditions, [.. trace]));

        var start = Math.Min(_maxSuffixLength, text.Length);

        for (var i = start; i > 0; i--)
        {
            var suffix = text[^i..];
            if (!_transforms.TryGetValue(suffix, out var rules))
                continue;

            var prefix = text[..^i];

            foreach (var rule in rules)
            {
                if (conditions != JapaneseDeinflectionConditions.None
                    && (conditions & rule.ConditionsIn) == 0)
                    continue;

                var transformed = prefix + rule.To;
                trace.Add(_groups[rule.GroupId]);
                DeinflectRecursive(transformed, rule.ConditionsOut, trace, results);
                trace.RemoveAt(trace.Count - 1);
            }
        }
    }

    private int AddGroup(TransformGroup group)
    {
        var id = _groups.Count;
        _groups.Add(group);
        return id;
    }

    private void AddRule(Rule rule)
    {
        if (!_transforms.TryGetValue(rule.From, out var list))
        {
            list = [];
            _transforms[rule.From] = list;
        }
        list.Add(rule);
        if (rule.From.Length > _maxSuffixLength)
            _maxSuffixLength = rule.From.Length;
    }

    private void AddIrregular(string suffix, JapaneseDeinflectionConditions conditionsIn,
        JapaneseDeinflectionConditions conditionsOut, int groupId)
    {
        foreach (var (verb, prefix) in IkuVerbs)
        {
            AddRule(new Rule(prefix + suffix, verb, conditionsIn, conditionsOut, groupId));
        }

        foreach (var verb in GodanUSpecialVerbs)
        {
            AddRule(new Rule(verb + suffix, verb, conditionsIn, conditionsOut, groupId));
        }

        foreach (var (verb, teRoot) in FuVerbTeConjugations)
        {
            AddRule(new Rule(teRoot + suffix, verb, conditionsIn, conditionsOut, groupId));
        }
    }

    private static readonly (string verb, string prefix)[] IkuVerbs =
    [
        ("いく", "いっ"),
        ("行く", "行っ"),
        ("逝く", "逝っ"),
        ("往く", "往っ"),
    ];

    private static readonly string[] GodanUSpecialVerbs =
    [
        "こう", "とう", "請う", "乞う", "恋う", "問う", "訪う", "宣う", "曰う", "給う", "賜う", "揺蕩う",
    ];

    private static readonly (string verb, string teRoot)[] FuVerbTeConjugations =
    [
        ("のたまう", "のたもう"),
        ("たまう", "たもう"),
        ("たゆたう", "たゆとう"),
    ];

    private void InitTransforms()
    {
        var none = JapaneseDeinflectionConditions.None;
        var v1 = JapaneseDeinflectionConditions.V1;
        var v1d = JapaneseDeinflectionConditions.V1D;
        var v1p = JapaneseDeinflectionConditions.V1P;
        var v5 = JapaneseDeinflectionConditions.V5;
        var v5d = JapaneseDeinflectionConditions.V5D;
        var v5s = JapaneseDeinflectionConditions.V5S;
        var v5ss = JapaneseDeinflectionConditions.V5SS;
        var v5sp = JapaneseDeinflectionConditions.V5SP;
        var vk = JapaneseDeinflectionConditions.VK;
        var vs = JapaneseDeinflectionConditions.VS;
        var vz = JapaneseDeinflectionConditions.VZ;
        var adjI = JapaneseDeinflectionConditions.ADJ_I;
        var masu = JapaneseDeinflectionConditions.MASU;
        var masen = JapaneseDeinflectionConditions.MASEN;
        var te = JapaneseDeinflectionConditions.TE;
        var ba = JapaneseDeinflectionConditions.BA;
        var ku = JapaneseDeinflectionConditions.KU;
        var ta = JapaneseDeinflectionConditions.TA;
        var nn = JapaneseDeinflectionConditions.NN;
        var nasai = JapaneseDeinflectionConditions.NASAI;
        var ya = JapaneseDeinflectionConditions.YA;
        var v = JapaneseDeinflectionConditions.V;

        // -ば
        var id = AddGroup(new TransformGroup("-ば",
            "1. Conditional form; shows that the previous stated condition's establishment is the condition for the latter stated condition to occur.\n2. Shows a trigger for a latter stated perception or judgment.\nUsage: Attach ば to the hypothetical form (仮定形) of verbs and i-adjectives."));
        AddRule(new("ければ", "い", ba, adjI, id));
        AddRule(new("えば", "う", ba, v5, id));
        AddRule(new("けば", "く", ba, v5, id));
        AddRule(new("げば", "ぐ", ba, v5, id));
        AddRule(new("せば", "す", ba, v5, id));
        AddRule(new("てば", "つ", ba, v5, id));
        AddRule(new("ねば", "ぬ", ba, v5, id));
        AddRule(new("べば", "ぶ", ba, v5, id));
        AddRule(new("めば", "む", ba, v5, id));
        AddRule(new("れば", "る", ba, v1 | v5 | vk | vs | vz, id));
        AddRule(new("れば", "", ba, masu, id));

        // -ゃ (contraction of -ば)
        id = AddGroup(new TransformGroup("-ゃ", "Contraction of -ば."));
        AddRule(new("けりゃ", "ければ", ya, ba, id));
        AddRule(new("きゃ", "ければ", ya, ba, id));
        AddRule(new("や", "えば", ya, ba, id));
        AddRule(new("きゃ", "けば", ya, ba, id));
        AddRule(new("ぎゃ", "げば", ya, ba, id));
        AddRule(new("しゃ", "せば", ya, ba, id));
        AddRule(new("ちゃ", "てば", ya, ba, id));
        AddRule(new("にゃ", "ねば", ya, ba, id));
        AddRule(new("びゃ", "べば", ya, ba, id));
        AddRule(new("みゃ", "めば", ya, ba, id));
        AddRule(new("りゃ", "れば", ya, ba, id));

        // -ちゃ
        id = AddGroup(new TransformGroup("-ちゃ",
            "Contraction of ～ては.\n1. Explains how something always happens under the condition that it marks.\n2. Expresses the repetition (of a series of) actions.\n3. Indicates a hypothetical situation in which the speaker gives a (negative) evaluation about the other party's intentions.\n4. Used in \"Must Not\" patterns like ～てはいけない.\nUsage: Attach は after the て-form of verbs, contract ては into ちゃ."));
        AddRule(new("ちゃ", "る", v5, v1, id));
        AddRule(new("いじゃ", "ぐ", v5, v5, id));
        AddRule(new("いちゃ", "く", v5, v5, id));
        AddRule(new("しちゃ", "す", v5, v5, id));
        AddRule(new("っちゃ", "う", v5, v5, id));
        AddRule(new("っちゃ", "く", v5, v5, id));
        AddRule(new("っちゃ", "つ", v5, v5, id));
        AddRule(new("っちゃ", "る", v5, v5, id));
        AddRule(new("んじゃ", "ぬ", v5, v5, id));
        AddRule(new("んじゃ", "ぶ", v5, v5, id));
        AddRule(new("んじゃ", "む", v5, v5, id));
        AddRule(new("じちゃ", "ずる", v5, vz, id));
        AddRule(new("しちゃ", "する", v5, vs, id));
        AddRule(new("為ちゃ", "為る", v5, vs, id));
        AddRule(new("きちゃ", "くる", v5, vk, id));
        AddRule(new("来ちゃ", "来る", v5, vk, id));
        AddRule(new("來ちゃ", "來る", v5, vk, id));

        // -ちゃう
        id = AddGroup(new TransformGroup("-ちゃう", "Contraction of -しまう.\n" + ShimauDescription + "Usage: Attach しまう after the て-form of verbs, contract てしまう into ちゃう."));
        AddRule(new("ちゃう", "る", v5, v1, id));
        AddRule(new("いじゃう", "ぐ", v5, v5, id));
        AddRule(new("いちゃう", "く", v5, v5, id));
        AddRule(new("しちゃう", "す", v5, v5, id));
        AddRule(new("っちゃう", "う", v5, v5, id));
        AddRule(new("っちゃう", "く", v5, v5, id));
        AddRule(new("っちゃう", "つ", v5, v5, id));
        AddRule(new("っちゃう", "る", v5, v5, id));
        AddRule(new("んじゃう", "ぬ", v5, v5, id));
        AddRule(new("んじゃう", "ぶ", v5, v5, id));
        AddRule(new("んじゃう", "む", v5, v5, id));
        AddRule(new("じちゃう", "ずる", v5, vz, id));
        AddRule(new("しちゃう", "する", v5, vs, id));
        AddRule(new("為ちゃう", "為る", v5, vs, id));
        AddRule(new("きちゃう", "くる", v5, vk, id));
        AddRule(new("来ちゃう", "来る", v5, vk, id));
        AddRule(new("來ちゃう", "來る", v5, vk, id));

        // -ちまう
        id = AddGroup(new TransformGroup("-ちまう", "Contraction of -しまう.\n" + ShimauDescription + "Usage: Attach しまう after the て-form of verbs, contract てしまう into ちまう."));
        AddRule(new("ちまう", "る", v5, v1, id));
        AddRule(new("いじまう", "ぐ", v5, v5, id));
        AddRule(new("いちまう", "く", v5, v5, id));
        AddRule(new("しちまう", "す", v5, v5, id));
        AddRule(new("っちまう", "う", v5, v5, id));
        AddRule(new("っちまう", "く", v5, v5, id));
        AddRule(new("っちまう", "つ", v5, v5, id));
        AddRule(new("っちまう", "る", v5, v5, id));
        AddRule(new("んじまう", "ぬ", v5, v5, id));
        AddRule(new("んじまう", "ぶ", v5, v5, id));
        AddRule(new("んじまう", "む", v5, v5, id));
        AddRule(new("じちまう", "ずる", v5, vz, id));
        AddRule(new("しちまう", "する", v5, vs, id));
        AddRule(new("為ちまう", "為る", v5, vs, id));
        AddRule(new("きちまう", "くる", v5, vk, id));
        AddRule(new("来ちまう", "来る", v5, vk, id));
        AddRule(new("來ちまう", "來る", v5, vk, id));

        // -しまう
        id = AddGroup(new TransformGroup("-しまう", ShimauDescription + "Usage: Attach しまう after the て-form of verbs."));
        AddRule(new("てしまう", "て", v5, te, id));
        AddRule(new("でしまう", "で", v5, te, id));

        // -なさい
        id = AddGroup(new TransformGroup("-なさい", "Polite imperative suffix.\nUsage: Attach なさい after the continuative form (連用形) of verbs."));
        AddRule(new("なさい", "る", nasai, v1, id));
        AddRule(new("いなさい", "う", nasai, v5, id));
        AddRule(new("きなさい", "く", nasai, v5, id));
        AddRule(new("ぎなさい", "ぐ", nasai, v5, id));
        AddRule(new("しなさい", "す", nasai, v5, id));
        AddRule(new("ちなさい", "つ", nasai, v5, id));
        AddRule(new("になさい", "ぬ", nasai, v5, id));
        AddRule(new("びなさい", "ぶ", nasai, v5, id));
        AddRule(new("みなさい", "む", nasai, v5, id));
        AddRule(new("りなさい", "る", nasai, v5, id));
        AddRule(new("じなさい", "ずる", nasai, vz, id));
        AddRule(new("しなさい", "する", nasai, vs, id));
        AddRule(new("為なさい", "為る", nasai, vs, id));
        AddRule(new("きなさい", "くる", nasai, vk, id));
        AddRule(new("来なさい", "来る", nasai, vk, id));
        AddRule(new("來なさい", "來る", nasai, vk, id));

        // -そう
        id = AddGroup(new TransformGroup("-そう", "Appearing that; looking like.\nUsage: Attach そう to the continuative form (連用形) of verbs, or to the stem of adjectives."));
        AddRule(new("そう", "い", none, adjI, id));
        AddRule(new("そう", "る", none, v1, id));
        AddRule(new("いそう", "う", none, v5, id));
        AddRule(new("きそう", "く", none, v5, id));
        AddRule(new("ぎそう", "ぐ", none, v5, id));
        AddRule(new("しそう", "す", none, v5, id));
        AddRule(new("ちそう", "つ", none, v5, id));
        AddRule(new("にそう", "ぬ", none, v5, id));
        AddRule(new("びそう", "ぶ", none, v5, id));
        AddRule(new("みそう", "む", none, v5, id));
        AddRule(new("りそう", "る", none, v5, id));
        AddRule(new("じそう", "ずる", none, vz, id));
        AddRule(new("しそう", "する", none, vs, id));
        AddRule(new("為そう", "為る", none, vs, id));
        AddRule(new("きそう", "くる", none, vk, id));
        AddRule(new("来そう", "来る", none, vk, id));
        AddRule(new("來そう", "來る", none, vk, id));

        // -すぎる
        id = AddGroup(new TransformGroup("-すぎる", "Shows something \"is too...\" or someone is doing something \"too much\".\nUsage: Attach すぎる to the continuative form (連用形) of verbs, or to the stem of adjectives."));
        AddRule(new("すぎる", "い", v1, adjI, id));
        AddRule(new("すぎる", "る", v1, v1, id));
        AddRule(new("いすぎる", "う", v1, v5, id));
        AddRule(new("きすぎる", "く", v1, v5, id));
        AddRule(new("ぎすぎる", "ぐ", v1, v5, id));
        AddRule(new("しすぎる", "す", v1, v5, id));
        AddRule(new("ちすぎる", "つ", v1, v5, id));
        AddRule(new("にすぎる", "ぬ", v1, v5, id));
        AddRule(new("びすぎる", "ぶ", v1, v5, id));
        AddRule(new("みすぎる", "む", v1, v5, id));
        AddRule(new("りすぎる", "る", v1, v5, id));
        AddRule(new("じすぎる", "ずる", v1, vz, id));
        AddRule(new("しすぎる", "する", v1, vs, id));
        AddRule(new("為すぎる", "為る", v1, vs, id));
        AddRule(new("きすぎる", "くる", v1, vk, id));
        AddRule(new("来すぎる", "来る", v1, vk, id));
        AddRule(new("來すぎる", "來る", v1, vk, id));

        // -過ぎる
        id = AddGroup(new TransformGroup("-過ぎる", "Shows something \"is too...\" or someone is doing something \"too much\".\nUsage: Attach すぎる to the continuative form (連用形) of verbs, or to the stem of adjectives."));
        AddRule(new("過ぎる", "い", v1, adjI, id));
        AddRule(new("過ぎる", "る", v1, v1, id));
        AddRule(new("い過ぎる", "う", v1, v5, id));
        AddRule(new("き過ぎる", "く", v1, v5, id));
        AddRule(new("ぎ過ぎる", "ぐ", v1, v5, id));
        AddRule(new("し過ぎる", "す", v1, v5, id));
        AddRule(new("ち過ぎる", "つ", v1, v5, id));
        AddRule(new("に過ぎる", "ぬ", v1, v5, id));
        AddRule(new("び過ぎる", "ぶ", v1, v5, id));
        AddRule(new("み過ぎる", "む", v1, v5, id));
        AddRule(new("り過ぎる", "る", v1, v5, id));
        AddRule(new("じ過ぎる", "ずる", v1, vz, id));
        AddRule(new("し過ぎる", "する", v1, vs, id));
        AddRule(new("為過ぎる", "為る", v1, vs, id));
        AddRule(new("き過ぎる", "くる", v1, vk, id));
        AddRule(new("来過ぎる", "来る", v1, vk, id));
        AddRule(new("來過ぎる", "來る", v1, vk, id));

        // -たい
        id = AddGroup(new TransformGroup("-たい", "1. Expresses the feeling of desire or hope.\n2. Used in ...たいと思います, an indirect way of saying what the speaker intends to do.\nUsage: Attach たい to the continuative form (連用形) of verbs. たい itself conjugates as i-adjective."));
        AddRule(new("たい", "る", adjI, v1, id));
        AddRule(new("いたい", "う", adjI, v5, id));
        AddRule(new("きたい", "く", adjI, v5, id));
        AddRule(new("ぎたい", "ぐ", adjI, v5, id));
        AddRule(new("したい", "す", adjI, v5, id));
        AddRule(new("ちたい", "つ", adjI, v5, id));
        AddRule(new("にたい", "ぬ", adjI, v5, id));
        AddRule(new("びたい", "ぶ", adjI, v5, id));
        AddRule(new("みたい", "む", adjI, v5, id));
        AddRule(new("りたい", "る", adjI, v5, id));
        AddRule(new("じたい", "ずる", adjI, vz, id));
        AddRule(new("したい", "する", adjI, vs, id));
        AddRule(new("為たい", "為る", adjI, vs, id));
        AddRule(new("きたい", "くる", adjI, vk, id));
        AddRule(new("来たい", "来る", adjI, vk, id));
        AddRule(new("來たい", "來る", adjI, vk, id));

        // -たら
        id = AddGroup(new TransformGroup("-たら", "1. Denotes the latter stated event is a continuation of the previous stated event.\n2. Assumes that a matter has been completed or concluded.\nUsage: Attach たら to the continuative form (連用形) of verbs after euphonic change form, かったら to the stem of i-adjectives."));
        AddRule(new("かったら", "い", none, adjI, id));
        AddRule(new("たら", "る", none, v1, id));
        AddRule(new("いたら", "く", none, v5, id));
        AddRule(new("いだら", "ぐ", none, v5, id));
        AddRule(new("したら", "す", none, v5, id));
        AddRule(new("ったら", "う", none, v5, id));
        AddRule(new("ったら", "つ", none, v5, id));
        AddRule(new("ったら", "る", none, v5, id));
        AddRule(new("んだら", "ぬ", none, v5, id));
        AddRule(new("んだら", "ぶ", none, v5, id));
        AddRule(new("んだら", "む", none, v5, id));
        AddRule(new("じたら", "ずる", none, vz, id));
        AddRule(new("したら", "する", none, vs, id));
        AddRule(new("為たら", "為る", none, vs, id));
        AddRule(new("きたら", "くる", none, vk, id));
        AddRule(new("来たら", "来る", none, vk, id));
        AddRule(new("來たら", "來る", none, vk, id));
        AddIrregular("たら", none, v5, id);
        AddRule(new("ましたら", "ます", none, masu, id));

        // -たり
        id = AddGroup(new TransformGroup("-たり", "1. Shows two actions occurring back and forth (when used with two verbs).\n2. Shows examples of actions and states (when used with multiple verbs and adjectives).\nUsage: Attach たり to the continuative form (連用形) of verbs after euphonic change form, かったり to the stem of i-adjectives"));
        AddRule(new("かったり", "い", none, adjI, id));
        AddRule(new("たり", "る", none, v1, id));
        AddRule(new("いたり", "く", none, v5, id));
        AddRule(new("いだり", "ぐ", none, v5, id));
        AddRule(new("したり", "す", none, v5, id));
        AddRule(new("ったり", "う", none, v5, id));
        AddRule(new("ったり", "つ", none, v5, id));
        AddRule(new("ったり", "る", none, v5, id));
        AddRule(new("んだり", "ぬ", none, v5, id));
        AddRule(new("んだり", "ぶ", none, v5, id));
        AddRule(new("んだり", "む", none, v5, id));
        AddRule(new("じたり", "ずる", none, vz, id));
        AddRule(new("したり", "する", none, vs, id));
        AddRule(new("為たり", "為る", none, vs, id));
        AddRule(new("きたり", "くる", none, vk, id));
        AddRule(new("来たり", "来る", none, vk, id));
        AddRule(new("來たり", "來る", none, vk, id));
        AddIrregular("たり", none, v5, id);

        // -て
        id = AddGroup(new TransformGroup("-て", "て-form.\nIt has a myriad of meanings. Primarily, it is a conjunctive particle that connects two clauses together.\nUsage: Attach て to the continuative form (連用形) of verbs after euphonic change form, くて to the stem of i-adjectives."));
        AddRule(new("くて", "い", te, adjI, id));
        AddRule(new("て", "る", te, v1, id));
        AddRule(new("いて", "く", te, v5, id));
        AddRule(new("いで", "ぐ", te, v5, id));
        AddRule(new("して", "す", te, v5, id));
        AddRule(new("って", "う", te, v5, id));
        AddRule(new("って", "つ", te, v5, id));
        AddRule(new("って", "る", te, v5, id));
        AddRule(new("んで", "ぬ", te, v5, id));
        AddRule(new("んで", "ぶ", te, v5, id));
        AddRule(new("んで", "む", te, v5, id));
        AddRule(new("じて", "ずる", te, vz, id));
        AddRule(new("して", "する", te, vs, id));
        AddRule(new("為て", "為る", te, vs, id));
        AddRule(new("きて", "くる", te, vk, id));
        AddRule(new("来て", "来る", te, vk, id));
        AddRule(new("來て", "來る", te, vk, id));
        AddIrregular("て", te, v5, id);
        AddRule(new("まして", "ます", none, masu, id));

        // -ず
        id = AddGroup(new TransformGroup("-ず", "1. Negative form of verbs.\n2. Continuative form (連用形) of the particle ぬ (nu).\nUsage: Attach ず to the irrealis form (未然形) of verbs."));
        AddRule(new("ず", "る", none, v1, id));
        AddRule(new("かず", "く", none, v5, id));
        AddRule(new("がず", "ぐ", none, v5, id));
        AddRule(new("さず", "す", none, v5, id));
        AddRule(new("たず", "つ", none, v5, id));
        AddRule(new("なず", "ぬ", none, v5, id));
        AddRule(new("ばず", "ぶ", none, v5, id));
        AddRule(new("まず", "む", none, v5, id));
        AddRule(new("らず", "る", none, v5, id));
        AddRule(new("わず", "う", none, v5, id));
        AddRule(new("ぜず", "ずる", none, vz, id));
        AddRule(new("せず", "する", none, vs, id));
        AddRule(new("為ず", "為る", none, vs, id));
        AddRule(new("こず", "くる", none, vk, id));
        AddRule(new("来ず", "来る", none, vk, id));
        AddRule(new("來ず", "來る", none, vk, id));

        // -ぬ
        id = AddGroup(new TransformGroup("-ぬ", "Negative form of verbs.\nUsage: Attach ぬ to the irrealis form (未然形) of verbs.\nする becomes せぬ"));
        AddRule(new("ぬ", "る", none, v1, id));
        AddRule(new("かぬ", "く", none, v5, id));
        AddRule(new("がぬ", "ぐ", none, v5, id));
        AddRule(new("さぬ", "す", none, v5, id));
        AddRule(new("たぬ", "つ", none, v5, id));
        AddRule(new("なぬ", "ぬ", none, v5, id));
        AddRule(new("ばぬ", "ぶ", none, v5, id));
        AddRule(new("まぬ", "む", none, v5, id));
        AddRule(new("らぬ", "る", none, v5, id));
        AddRule(new("わぬ", "う", none, v5, id));
        AddRule(new("ぜぬ", "ずる", none, vz, id));
        AddRule(new("せぬ", "する", none, vs, id));
        AddRule(new("為ぬ", "為る", none, vs, id));
        AddRule(new("こぬ", "くる", none, vk, id));
        AddRule(new("来ぬ", "来る", none, vk, id));
        AddRule(new("來ぬ", "來る", none, vk, id));

        // -ん
        id = AddGroup(new TransformGroup("-ん", "Negative form of verbs; a sound change of ぬ.\nUsage: Attach ん to the irrealis form (未然形) of verbs.\nする becomes せん"));
        AddRule(new("ん", "る", nn, v1, id));
        AddRule(new("かん", "く", nn, v5, id));
        AddRule(new("がん", "ぐ", nn, v5, id));
        AddRule(new("さん", "す", nn, v5, id));
        AddRule(new("たん", "つ", nn, v5, id));
        AddRule(new("なん", "ぬ", nn, v5, id));
        AddRule(new("ばん", "ぶ", nn, v5, id));
        AddRule(new("まん", "む", nn, v5, id));
        AddRule(new("らん", "る", nn, v5, id));
        AddRule(new("わん", "う", nn, v5, id));
        AddRule(new("ぜん", "ずる", nn, vz, id));
        AddRule(new("せん", "する", nn, vs, id));
        AddRule(new("為ん", "為る", nn, vs, id));
        AddRule(new("こん", "くる", nn, vk, id));
        AddRule(new("来ん", "来る", nn, vk, id));
        AddRule(new("來ん", "來る", nn, vk, id));

        // -んばかり
        id = AddGroup(new TransformGroup("-んばかり", "Shows an action or condition is on the verge of occurring, or an excessive/extreme degree.\nUsage: Attach んばかり to the irrealis form (未然形) of verbs.\nする becomes せんばかり"));
        AddRule(new("んばかり", "る", none, v1, id));
        AddRule(new("かんばかり", "く", none, v5, id));
        AddRule(new("がんばかり", "ぐ", none, v5, id));
        AddRule(new("さんばかり", "す", none, v5, id));
        AddRule(new("たんばかり", "つ", none, v5, id));
        AddRule(new("なんばかり", "ぬ", none, v5, id));
        AddRule(new("ばんばかり", "ぶ", none, v5, id));
        AddRule(new("まんばかり", "む", none, v5, id));
        AddRule(new("らんばかり", "る", none, v5, id));
        AddRule(new("わんばかり", "う", none, v5, id));
        AddRule(new("ぜんばかり", "ずる", none, vz, id));
        AddRule(new("せんばかり", "する", none, vs, id));
        AddRule(new("為んばかり", "為る", none, vs, id));
        AddRule(new("こんばかり", "くる", none, vk, id));
        AddRule(new("来んばかり", "来る", none, vk, id));
        AddRule(new("來んばかり", "來る", none, vk, id));

        // -んとする
        id = AddGroup(new TransformGroup("-んとする", "1. Shows the speaker's will or intention.\n2. Shows an action or condition is on the verge of occurring.\nUsage: Attach んとする to the irrealis form (未然形) of verbs.\nする becomes せんとする"));
        AddRule(new("んとする", "る", vs, v1, id));
        AddRule(new("かんとする", "く", vs, v5, id));
        AddRule(new("がんとする", "ぐ", vs, v5, id));
        AddRule(new("さんとする", "す", vs, v5, id));
        AddRule(new("たんとする", "つ", vs, v5, id));
        AddRule(new("なんとする", "ぬ", vs, v5, id));
        AddRule(new("ばんとする", "ぶ", vs, v5, id));
        AddRule(new("まんとする", "む", vs, v5, id));
        AddRule(new("らんとする", "る", vs, v5, id));
        AddRule(new("わんとする", "う", vs, v5, id));
        AddRule(new("ぜんとする", "ずる", vs, vz, id));
        AddRule(new("せんとする", "する", vs, vs, id));
        AddRule(new("為んとする", "為る", vs, vs, id));
        AddRule(new("こんとする", "くる", vs, vk, id));
        AddRule(new("来んとする", "来る", vs, vk, id));
        AddRule(new("來んとする", "來る", vs, vk, id));

        // -む
        id = AddGroup(new TransformGroup("-む", "Archaic.\n1. Shows an inference of a certain matter.\n2. Shows speaker's intention.\nUsage: Attach む to the irrealis form (未然形) of verbs.\nする becomes せむ"));
        AddRule(new("む", "る", none, v1, id));
        AddRule(new("かむ", "く", none, v5, id));
        AddRule(new("がむ", "ぐ", none, v5, id));
        AddRule(new("さむ", "す", none, v5, id));
        AddRule(new("たむ", "つ", none, v5, id));
        AddRule(new("なむ", "ぬ", none, v5, id));
        AddRule(new("ばむ", "ぶ", none, v5, id));
        AddRule(new("まむ", "む", none, v5, id));
        AddRule(new("らむ", "る", none, v5, id));
        AddRule(new("わむ", "う", none, v5, id));
        AddRule(new("ぜむ", "ずる", none, vz, id));
        AddRule(new("せむ", "する", none, vs, id));
        AddRule(new("為む", "為る", none, vs, id));
        AddRule(new("こむ", "くる", none, vk, id));
        AddRule(new("来む", "来る", none, vk, id));
        AddRule(new("來む", "來る", none, vk, id));

        // -ざる
        id = AddGroup(new TransformGroup("-ざる", "Negative form of verbs.\nUsage: Attach ざる to the irrealis form (未然形) of verbs.\nする becomes せざる"));
        AddRule(new("ざる", "る", none, v1, id));
        AddRule(new("かざる", "く", none, v5, id));
        AddRule(new("がざる", "ぐ", none, v5, id));
        AddRule(new("さざる", "す", none, v5, id));
        AddRule(new("たざる", "つ", none, v5, id));
        AddRule(new("なざる", "ぬ", none, v5, id));
        AddRule(new("ばざる", "ぶ", none, v5, id));
        AddRule(new("まざる", "む", none, v5, id));
        AddRule(new("らざる", "る", none, v5, id));
        AddRule(new("わざる", "う", none, v5, id));
        AddRule(new("ぜざる", "ずる", none, vz, id));
        AddRule(new("せざる", "する", none, vs, id));
        AddRule(new("為ざる", "為る", none, vs, id));
        AddRule(new("こざる", "くる", none, vk, id));
        AddRule(new("来ざる", "来る", none, vk, id));
        AddRule(new("來ざる", "來る", none, vk, id));

        // -ねば
        id = AddGroup(new TransformGroup("-ねば", "1. Shows a hypothetical negation; if not ...\n2. Shows a must. Used with or without ならぬ.\nUsage: Attach ねば to the irrealis form (未然形) of verbs.\nする becomes せねば"));
        AddRule(new("ねば", "る", ba, v1, id));
        AddRule(new("かねば", "く", ba, v5, id));
        AddRule(new("がねば", "ぐ", ba, v5, id));
        AddRule(new("さねば", "す", ba, v5, id));
        AddRule(new("たねば", "つ", ba, v5, id));
        AddRule(new("なねば", "ぬ", ba, v5, id));
        AddRule(new("ばねば", "ぶ", ba, v5, id));
        AddRule(new("まねば", "む", ba, v5, id));
        AddRule(new("らねば", "る", ba, v5, id));
        AddRule(new("わねば", "う", ba, v5, id));
        AddRule(new("ぜねば", "ずる", ba, vz, id));
        AddRule(new("せねば", "する", ba, vs, id));
        AddRule(new("為ねば", "為る", ba, vs, id));
        AddRule(new("こねば", "くる", ba, vk, id));
        AddRule(new("来ねば", "来る", ba, vk, id));
        AddRule(new("來ねば", "來る", ba, vk, id));

        // -く
        id = AddGroup(new TransformGroup("-く", "Adverbial form of i-adjectives."));
        AddRule(new("く", "い", ku, adjI, id));

        // causative
        id = AddGroup(new TransformGroup("causative", "Describes the intention to make someone do something.\nUsage: Attach させる to the irrealis form (未然形) of ichidan verbs and くる.\nAttach せる to the irrealis form (未然形) of godan verbs and する.\nIt itself conjugates as an ichidan verb."));
        AddRule(new("させる", "る", v1, v1, id));
        AddRule(new("かせる", "く", v1, v5, id));
        AddRule(new("がせる", "ぐ", v1, v5, id));
        AddRule(new("させる", "す", v1, v5, id));
        AddRule(new("たせる", "つ", v1, v5, id));
        AddRule(new("なせる", "ぬ", v1, v5, id));
        AddRule(new("ばせる", "ぶ", v1, v5, id));
        AddRule(new("ませる", "む", v1, v5, id));
        AddRule(new("らせる", "る", v1, v5, id));
        AddRule(new("わせる", "う", v1, v5, id));
        AddRule(new("じさせる", "ずる", v1, vz, id));
        AddRule(new("ぜさせる", "ずる", v1, vz, id));
        AddRule(new("させる", "する", v1, vs, id));
        AddRule(new("為せる", "為る", v1, vs, id));
        AddRule(new("せさせる", "する", v1, vs, id));
        AddRule(new("為させる", "為る", v1, vs, id));
        AddRule(new("こさせる", "くる", v1, vk, id));
        AddRule(new("来させる", "来る", v1, vk, id));
        AddRule(new("來させる", "來る", v1, vk, id));

        // short causative
        id = AddGroup(new TransformGroup("short causative", "Contraction of the causative form.\nDescribes the intention to make someone do something.\nUsage: Attach す to the irrealis form (未然形) of godan verbs.\nAttach さす to the dictionary form (終止形) of ichidan verbs.\nする becomes さす, くる becomes こさす.\nIt itself conjugates as an godan verb."));
        AddRule(new("さす", "る", v5ss, v1, id));
        AddRule(new("かす", "く", v5sp, v5, id));
        AddRule(new("がす", "ぐ", v5sp, v5, id));
        AddRule(new("さす", "す", v5ss, v5, id));
        AddRule(new("たす", "つ", v5sp, v5, id));
        AddRule(new("なす", "ぬ", v5sp, v5, id));
        AddRule(new("ばす", "ぶ", v5sp, v5, id));
        AddRule(new("ます", "む", v5sp, v5, id));
        AddRule(new("らす", "る", v5sp, v5, id));
        AddRule(new("わす", "う", v5sp, v5, id));
        AddRule(new("じさす", "ずる", v5ss, vz, id));
        AddRule(new("ぜさす", "ずる", v5ss, vz, id));
        AddRule(new("さす", "する", v5ss, vs, id));
        AddRule(new("為す", "為る", v5ss, vs, id));
        AddRule(new("こさす", "くる", v5ss, vk, id));
        AddRule(new("来さす", "来る", v5ss, vk, id));
        AddRule(new("來さす", "來る", v5ss, vk, id));

        // imperative
        id = AddGroup(new TransformGroup("imperative", "1. To give orders.\n2. (As あれ) Represents the fact that it will never change no matter the circumstances.\n3. Express a feeling of hope."));
        AddRule(new("ろ", "る", none, v1, id));
        AddRule(new("よ", "る", none, v1, id));
        AddRule(new("え", "う", none, v5, id));
        AddRule(new("け", "く", none, v5, id));
        AddRule(new("げ", "ぐ", none, v5, id));
        AddRule(new("せ", "す", none, v5, id));
        AddRule(new("て", "つ", none, v5, id));
        AddRule(new("ね", "ぬ", none, v5, id));
        AddRule(new("べ", "ぶ", none, v5, id));
        AddRule(new("め", "む", none, v5, id));
        AddRule(new("れ", "る", none, v5, id));
        AddRule(new("じろ", "ずる", none, vz, id));
        AddRule(new("ぜよ", "ずる", none, vz, id));
        AddRule(new("しろ", "する", none, vs, id));
        AddRule(new("せよ", "する", none, vs, id));
        AddRule(new("為ろ", "為る", none, vs, id));
        AddRule(new("為よ", "為る", none, vs, id));
        AddRule(new("こい", "くる", none, vk, id));
        AddRule(new("来い", "来る", none, vk, id));
        AddRule(new("來い", "來る", none, vk, id));
        AddRule(new("ませ", "ます", none, masu, id));
        AddRule(new("くれ", "くれる", none, v1, id));

        // continuative
        id = AddGroup(new TransformGroup("continuative", "Used to indicate actions that are (being) carried out.\nRefers to 連用形, the part of the verb after conjugating with -ます and dropping ます."));
        AddRule(new("い", "いる", none, v1d, id));
        AddRule(new("え", "える", none, v1d, id));
        AddRule(new("き", "きる", none, v1d, id));
        AddRule(new("ぎ", "ぎる", none, v1d, id));
        AddRule(new("け", "ける", none, v1d, id));
        AddRule(new("げ", "げる", none, v1d, id));
        AddRule(new("じ", "じる", none, v1d, id));
        AddRule(new("せ", "せる", none, v1d, id));
        AddRule(new("ぜ", "ぜる", none, v1d, id));
        AddRule(new("ち", "ちる", none, v1d, id));
        AddRule(new("て", "てる", none, v1d, id));
        AddRule(new("で", "でる", none, v1d, id));
        AddRule(new("に", "にる", none, v1d, id));
        AddRule(new("ね", "ねる", none, v1d, id));
        AddRule(new("ひ", "ひる", none, v1d, id));
        AddRule(new("び", "びる", none, v1d, id));
        AddRule(new("へ", "へる", none, v1d, id));
        AddRule(new("べ", "べる", none, v1d, id));
        AddRule(new("み", "みる", none, v1d, id));
        AddRule(new("め", "める", none, v1d, id));
        AddRule(new("り", "りる", none, v1d, id));
        AddRule(new("れ", "れる", none, v1d, id));
        AddRule(new("い", "う", none, v5, id));
        AddRule(new("き", "く", none, v5, id));
        AddRule(new("ぎ", "ぐ", none, v5, id));
        AddRule(new("し", "す", none, v5, id));
        AddRule(new("ち", "つ", none, v5, id));
        AddRule(new("に", "ぬ", none, v5, id));
        AddRule(new("び", "ぶ", none, v5, id));
        AddRule(new("み", "む", none, v5, id));
        AddRule(new("り", "る", none, v5, id));
        AddRule(new("き", "くる", none, vk, id));
        AddRule(new("し", "する", none, vs, id));
        AddRule(new("来", "来る", none, vk, id));
        AddRule(new("來", "來る", none, vk, id));

        // negative
        id = AddGroup(new TransformGroup("negative", "1. Negative form of verbs.\n2. Expresses a feeling of solicitation to the other party.\nUsage: Attach ない to the irrealis form (未然形) of verbs, くない to the stem of i-adjectives. ない itself conjugates as i-adjective. ます becomes ません."));
        AddRule(new("くない", "い", adjI, adjI, id));
        AddRule(new("ない", "る", adjI, v1, id));
        AddRule(new("かない", "く", adjI, v5, id));
        AddRule(new("がない", "ぐ", adjI, v5, id));
        AddRule(new("さない", "す", adjI, v5, id));
        AddRule(new("たない", "つ", adjI, v5, id));
        AddRule(new("なない", "ぬ", adjI, v5, id));
        AddRule(new("ばない", "ぶ", adjI, v5, id));
        AddRule(new("まない", "む", adjI, v5, id));
        AddRule(new("らない", "る", adjI, v5, id));
        AddRule(new("わない", "う", adjI, v5, id));
        AddRule(new("じない", "ずる", adjI, vz, id));
        AddRule(new("しない", "する", adjI, vs, id));
        AddRule(new("為ない", "為る", adjI, vs, id));
        AddRule(new("こない", "くる", adjI, vk, id));
        AddRule(new("来ない", "来る", adjI, vk, id));
        AddRule(new("來ない", "來る", adjI, vk, id));
        AddRule(new("ません", "ます", masen, masu, id));

        // -さ
        id = AddGroup(new TransformGroup("-さ", "Nominalizing suffix of i-adjectives indicating nature, state, mind or degree.\nUsage: Attach さ to the stem of i-adjectives."));
        AddRule(new("さ", "い", none, adjI, id));

        // passive
        id = AddGroup(new TransformGroup("passive", PassiveDescription + "Usage: Attach れる to the irrealis form (未然形) of godan verbs."));
        AddRule(new("かれる", "く", v1, v5, id));
        AddRule(new("がれる", "ぐ", v1, v5, id));
        AddRule(new("される", "す", v1, v5d | v5sp, id));
        AddRule(new("たれる", "つ", v1, v5, id));
        AddRule(new("なれる", "ぬ", v1, v5, id));
        AddRule(new("ばれる", "ぶ", v1, v5, id));
        AddRule(new("まれる", "む", v1, v5, id));
        AddRule(new("われる", "う", v1, v5, id));
        AddRule(new("られる", "る", v1, v5, id));
        AddRule(new("じされる", "ずる", v1, vz, id));
        AddRule(new("ぜされる", "ずる", v1, vz, id));
        AddRule(new("される", "する", v1, vs, id));
        AddRule(new("為れる", "為る", v1, vs, id));
        AddRule(new("こられる", "くる", v1, vk, id));
        AddRule(new("来られる", "来る", v1, vk, id));
        AddRule(new("來られる", "來る", v1, vk, id));

        // -た
        id = AddGroup(new TransformGroup("-た", "1. Indicates a reality that has happened in the past.\n2. Indicates the completion of an action.\n3. Indicates the confirmation of a matter.\n4. Indicates the speaker's confidence that the action will definitely be fulfilled.\n5. Indicates the events that occur before the main clause are represented as relative past.\n6. Indicates a mild imperative/command.\nUsage: Attach た to the continuative form (連用形) of verbs after euphonic change form, かった to the stem of i-adjectives."));
        AddRule(new("かった", "い", ta, adjI, id));
        AddRule(new("た", "る", ta, v1, id));
        AddRule(new("いた", "く", ta, v5, id));
        AddRule(new("いだ", "ぐ", ta, v5, id));
        AddRule(new("した", "す", ta, v5, id));
        AddRule(new("った", "う", ta, v5, id));
        AddRule(new("った", "つ", ta, v5, id));
        AddRule(new("った", "る", ta, v5, id));
        AddRule(new("んだ", "ぬ", ta, v5, id));
        AddRule(new("んだ", "ぶ", ta, v5, id));
        AddRule(new("んだ", "む", ta, v5, id));
        AddRule(new("じた", "ずる", ta, vz, id));
        AddRule(new("した", "する", ta, vs, id));
        AddRule(new("為た", "為る", ta, vs, id));
        AddRule(new("きた", "くる", ta, vk, id));
        AddRule(new("来た", "来る", ta, vk, id));
        AddRule(new("來た", "來る", ta, vk, id));
        AddIrregular("た", ta, v5, id);
        AddRule(new("ました", "ます", ta, masu, id));
        AddRule(new("でした", "", ta, masen, id));
        AddRule(new("かった", "", ta, masen | nn, id));

        // -ます
        id = AddGroup(new TransformGroup("-ます", "Polite conjugation of verbs and adjectives.\nUsage: Attach ます to the continuative form (連用形) of verbs."));
        AddRule(new("ます", "る", masu, v1, id));
        AddRule(new("います", "う", masu, v5d, id));
        AddRule(new("きます", "く", masu, v5d, id));
        AddRule(new("ぎます", "ぐ", masu, v5d, id));
        AddRule(new("します", "す", masu, v5d | v5s, id));
        AddRule(new("ちます", "つ", masu, v5d, id));
        AddRule(new("にます", "ぬ", masu, v5d, id));
        AddRule(new("びます", "ぶ", masu, v5d, id));
        AddRule(new("みます", "む", masu, v5d, id));
        AddRule(new("ります", "る", masu, v5d, id));
        AddRule(new("じます", "ずる", masu, vz, id));
        AddRule(new("します", "する", masu, vs, id));
        AddRule(new("為ます", "為る", masu, vs, id));
        AddRule(new("きます", "くる", masu, vk, id));
        AddRule(new("来ます", "来る", masu, vk, id));
        AddRule(new("來ます", "來る", masu, vk, id));
        AddRule(new("くあります", "い", masu, adjI, id));
        AddRule(new("くださいます", "くださる", masu, v5, id));
        AddRule(new("下さいます", "下さる", masu, v5, id));
        AddRule(new("いらっしゃいます", "いらっしゃる", masu, v5, id));
        AddRule(new("ございます", "ござる", masu, v5, id));
        AddRule(new("なさいます", "なさる", masu, v5, id));
        AddRule(new("おっしゃいます", "おっしゃる", masu, v5, id));
        AddRule(new("仰います", "仰る", masu, v5, id));
        AddRule(new("仰有います", "仰有る", masu, v5, id));

        // potential
        id = AddGroup(new TransformGroup("potential", "Indicates a state of being (naturally) capable of doing an action.\nUsage: Attach (ら)れる to the irrealis form (未然形) of ichidan verbs.\nAttach る to the imperative form (命令形) of godan verbs.\nする becomes できる, くる becomes こ(ら)れる"));
        AddRule(new("れる", "る", v1, v1 | v5d, id));
        AddRule(new("える", "う", v1, v5d, id));
        AddRule(new("ける", "く", v1, v5d, id));
        AddRule(new("げる", "ぐ", v1, v5d, id));
        AddRule(new("せる", "す", v1, v5d, id));
        AddRule(new("てる", "つ", v1, v5d, id));
        AddRule(new("ねる", "ぬ", v1, v5d, id));
        AddRule(new("べる", "ぶ", v1, v5d, id));
        AddRule(new("める", "む", v1, v5d, id));
        AddRule(new("できる", "する", v1, vs, id));
        AddRule(new("出来る", "する", v1, vs, id));
        AddRule(new("これる", "くる", v1, vk, id));
        AddRule(new("来れる", "来る", v1, vk, id));
        AddRule(new("來れる", "來る", v1, vk, id));

        // potential or passive
        id = AddGroup(new TransformGroup("potential or passive", PassiveDescription + "3. Indicates a state of being (naturally) capable of doing an action.\nUsage: Attach られる to the irrealis form (未然形) of ichidan verbs.\nする becomes せられる, くる becomes こられる"));
        AddRule(new("られる", "る", v1, v1, id));
        AddRule(new("ざれる", "ずる", v1, vz, id));
        AddRule(new("ぜられる", "ずる", v1, vz, id));
        AddRule(new("せられる", "する", v1, vs, id));
        AddRule(new("為られる", "為る", v1, vs, id));
        AddRule(new("こられる", "くる", v1, vk, id));
        AddRule(new("来られる", "来る", v1, vk, id));
        AddRule(new("來られる", "來る", v1, vk, id));

        // volitional
        id = AddGroup(new TransformGroup("volitional", "1. Expresses speaker's will or intention.\n2. Expresses an invitation to the other party.\n3. (Used in …ようとする) Indicates being on the verge of initiating an action or transforming a state.\n4. Indicates an inference of a matter.\nUsage: Attach よう to the irrealis form (未然形) of ichidan verbs.\nAttach う to the irrealis form (未然形) of godan verbs after -o euphonic change form.\nAttach かろう to the stem of i-adjectives (4th meaning only)."));
        AddRule(new("よう", "る", none, v1, id));
        AddRule(new("おう", "う", none, v5, id));
        AddRule(new("こう", "く", none, v5, id));
        AddRule(new("ごう", "ぐ", none, v5, id));
        AddRule(new("そう", "す", none, v5, id));
        AddRule(new("とう", "つ", none, v5, id));
        AddRule(new("のう", "ぬ", none, v5, id));
        AddRule(new("ぼう", "ぶ", none, v5, id));
        AddRule(new("もう", "む", none, v5, id));
        AddRule(new("ろう", "る", none, v5, id));
        AddRule(new("じよう", "ずる", none, vz, id));
        AddRule(new("しよう", "する", none, vs, id));
        AddRule(new("為よう", "為る", none, vs, id));
        AddRule(new("こよう", "くる", none, vk, id));
        AddRule(new("来よう", "来る", none, vk, id));
        AddRule(new("來よう", "來る", none, vk, id));
        AddRule(new("ましょう", "ます", none, masu, id));
        AddRule(new("かろう", "い", none, adjI, id));

        // volitional slang
        id = AddGroup(new TransformGroup("volitional slang", "Contraction of volitional form + か\n1. Expresses speaker's will or intention.\n2. Expresses an invitation to the other party.\nUsage: Replace final う with っ of volitional form then add か.\nFor example: 行こうか -> 行こっか."));
        AddRule(new("よっか", "る", none, v1, id));
        AddRule(new("おっか", "う", none, v5, id));
        AddRule(new("こっか", "く", none, v5, id));
        AddRule(new("ごっか", "ぐ", none, v5, id));
        AddRule(new("そっか", "す", none, v5, id));
        AddRule(new("とっか", "つ", none, v5, id));
        AddRule(new("のっか", "ぬ", none, v5, id));
        AddRule(new("ぼっか", "ぶ", none, v5, id));
        AddRule(new("もっか", "む", none, v5, id));
        AddRule(new("ろっか", "る", none, v5, id));
        AddRule(new("じよっか", "ずる", none, vz, id));
        AddRule(new("しよっか", "する", none, vs, id));
        AddRule(new("為よっか", "為る", none, vs, id));
        AddRule(new("こよっか", "くる", none, vk, id));
        AddRule(new("来よっか", "来る", none, vk, id));
        AddRule(new("來よっか", "來る", none, vk, id));
        AddRule(new("ましょっか", "ます", none, masu, id));

        // -まい
        id = AddGroup(new TransformGroup("-まい", "Negative volitional form of verbs.\n1. Expresses speaker's assumption that something is likely not true.\n2. Expresses speaker's will or intention not to do something.\nUsage: Attach まい to the dictionary form (終止形) of verbs.\nAttach まい to the irrealis form (未然形) of ichidan verbs.\nする becomes しまい, くる becomes こまい"));
        AddRule(new("まい", "", none, v, id));
        AddRule(new("まい", "る", none, v1, id));
        AddRule(new("じまい", "ずる", none, vz, id));
        AddRule(new("しまい", "する", none, vs, id));
        AddRule(new("為まい", "為る", none, vs, id));
        AddRule(new("こまい", "くる", none, vk, id));
        AddRule(new("来まい", "来る", none, vk, id));
        AddRule(new("來まい", "來る", none, vk, id));
        AddRule(new("まい", "", none, masu, id));

        // -おく
        id = AddGroup(new TransformGroup("-おく", "To do certain things in advance in preparation (or in anticipation) of latter needs.\nUsage: Attach おく to the て-form of verbs.\nAttach でおく after ない negative form of verbs.\nContracts to とく・どく in speech."));
        AddRule(new("ておく", "て", v5, te, id));
        AddRule(new("でおく", "で", v5, te, id));
        AddRule(new("とく", "て", v5, te, id));
        AddRule(new("どく", "で", v5, te, id));
        AddRule(new("ないでおく", "ない", v5, adjI, id));
        AddRule(new("ないどく", "ない", v5, adjI, id));

        // -いる
        id = AddGroup(new TransformGroup("-いる", "1. Indicates an action continues or progresses to a point in time.\n2. Indicates an action is completed and remains as is.\n3. Indicates a state or condition that can be taken to be the result of undergoing some change.\nUsage: Attach いる to the て-form of verbs. い can be dropped in speech.\nAttach でいる after ない negative form of verbs.\n(Slang) Attach おる to the て-form of verbs. Contracts to とる・でる in speech."));
        AddRule(new("ている", "て", v1, te, id));
        AddRule(new("ておる", "て", v5, te, id));
        AddRule(new("てる", "て", v1p, te, id));
        AddRule(new("でいる", "で", v1, te, id));
        AddRule(new("でおる", "で", v5, te, id));
        AddRule(new("でる", "で", v1p, te, id));
        AddRule(new("とる", "て", v5, te, id));
        AddRule(new("ないでいる", "ない", v1, adjI, id));

        // -き
        id = AddGroup(new TransformGroup("-き", "Attributive form (連体形) of i-adjectives. An archaic form that remains in modern Japanese."));
        AddRule(new("き", "い", none, adjI, id));

        // -げ
        id = AddGroup(new TransformGroup("-げ", "Describes a person's appearance. Shows feelings of the person.\nUsage: Attach げ or 気 to the stem of i-adjectives"));
        AddRule(new("げ", "い", none, adjI, id));
        AddRule(new("気", "い", none, adjI, id));

        // -がる
        id = AddGroup(new TransformGroup("-がる", "1. Shows subject's feelings contrast with what is thought/known about them.\n2. Indicates subject's behavior (stands out).\nUsage: Attach がる to the stem of i-adjectives. It itself conjugates as a godan verb."));
        AddRule(new("がる", "い", v5, adjI, id));

        // -え (slang i-adjective sound changes)
        id = AddGroup(new TransformGroup("-え", "Slang. A sound change of i-adjectives.\nai：やばい → やべぇ\nui：さむい → さみぃ/さめぇ\noi：すごい → すげぇ"));
        AddRule(new("ねえ", "ない", none, adjI, id));
        AddRule(new("めえ", "むい", none, adjI, id));
        AddRule(new("みい", "むい", none, adjI, id));
        AddRule(new("ちぇえ", "つい", none, adjI, id));
        AddRule(new("ちい", "つい", none, adjI, id));
        AddRule(new("せえ", "すい", none, adjI, id));
        AddRule(new("ええ", "いい", none, adjI, id));
        AddRule(new("ええ", "わい", none, adjI, id));
        AddRule(new("ええ", "よい", none, adjI, id));
        AddRule(new("いぇえ", "よい", none, adjI, id));
        AddRule(new("うぇえ", "わい", none, adjI, id));
        AddRule(new("けえ", "かい", none, adjI, id));
        AddRule(new("げえ", "がい", none, adjI, id));
        AddRule(new("げえ", "ごい", none, adjI, id));
        AddRule(new("せえ", "さい", none, adjI, id));
        AddRule(new("めえ", "まい", none, adjI, id));
        AddRule(new("ぜえ", "ずい", none, adjI, id));
        AddRule(new("っぜえ", "ずい", none, adjI, id));
        AddRule(new("れえ", "らい", none, adjI, id));
        AddRule(new("ちぇえ", "ちゃい", none, adjI, id));
        AddRule(new("でえ", "どい", none, adjI, id));
        AddRule(new("れえ", "れい", none, adjI, id));
        AddRule(new("べえ", "ばい", none, adjI, id));
        AddRule(new("てえ", "たい", none, adjI, id));
        AddRule(new("ねぇ", "ない", none, adjI, id));
        AddRule(new("めぇ", "むい", none, adjI, id));
        AddRule(new("みぃ", "むい", none, adjI, id));
        AddRule(new("ちぃ", "つい", none, adjI, id));
        AddRule(new("せぇ", "すい", none, adjI, id));
        AddRule(new("けぇ", "かい", none, adjI, id));
        AddRule(new("げぇ", "がい", none, adjI, id));
        AddRule(new("げぇ", "ごい", none, adjI, id));
        AddRule(new("せぇ", "さい", none, adjI, id));
        AddRule(new("めぇ", "まい", none, adjI, id));
        AddRule(new("ぜぇ", "ずい", none, adjI, id));
        AddRule(new("っぜぇ", "ずい", none, adjI, id));
        AddRule(new("れぇ", "らい", none, adjI, id));
        AddRule(new("でぇ", "どい", none, adjI, id));
        AddRule(new("れぇ", "れい", none, adjI, id));
        AddRule(new("べぇ", "ばい", none, adjI, id));
        AddRule(new("てぇ", "たい", none, adjI, id));

        // n-slang
        id = AddGroup(new TransformGroup("n-slang", ""));
        AddRule(new("んなさい", "りなさい", none, nasai, id));
        AddRule(new("らんない", "られない", adjI, adjI, id));
        AddRule(new("んない", "らない", adjI, adjI, id));
        AddRule(new("んなきゃ", "らなきゃ", none, ya, id));
        AddRule(new("んなきゃ", "れなきゃ", none, ya, id));

        // imperative negative slang
        id = AddGroup(new TransformGroup("imperative negative slang", ""));
        AddRule(new("んな", "る", none, v, id));

        // kansai-ben negative
        id = AddGroup(new TransformGroup("kansai-ben negative", "Negative form of kansai-ben verbs"));
        AddRule(new("へん", "ない", none, adjI, id));
        AddRule(new("ひん", "ない", none, adjI, id));
        AddRule(new("せえへん", "しない", none, adjI, id));
        AddRule(new("へんかった", "なかった", ta, ta, id));
        AddRule(new("ひんかった", "なかった", ta, ta, id));
        AddRule(new("うてへん", "ってない", none, adjI, id));

        // kansai-ben -て
        id = AddGroup(new TransformGroup("kansai-ben -て", "-て form of kansai-ben verbs"));
        AddRule(new("うて", "って", te, te, id));
        AddRule(new("おうて", "あって", te, te, id));
        AddRule(new("こうて", "かって", te, te, id));
        AddRule(new("ごうて", "がって", te, te, id));
        AddRule(new("そうて", "さって", te, te, id));
        AddRule(new("ぞうて", "ざって", te, te, id));
        AddRule(new("とうて", "たって", te, te, id));
        AddRule(new("どうて", "だって", te, te, id));
        AddRule(new("のうて", "なって", te, te, id));
        AddRule(new("ほうて", "はって", te, te, id));
        AddRule(new("ぼうて", "ばって", te, te, id));
        AddRule(new("もうて", "まって", te, te, id));
        AddRule(new("ろうて", "らって", te, te, id));
        AddRule(new("ようて", "やって", te, te, id));
        AddRule(new("ゆうて", "いって", te, te, id));

        // kansai-ben -た
        id = AddGroup(new TransformGroup("kansai-ben -た", "-た form of kansai-ben terms"));
        AddRule(new("うた", "った", ta, ta, id));
        AddRule(new("おうた", "あった", ta, ta, id));
        AddRule(new("こうた", "かった", ta, ta, id));
        AddRule(new("ごうた", "がった", ta, ta, id));
        AddRule(new("そうた", "さった", ta, ta, id));
        AddRule(new("ぞうた", "ざった", ta, ta, id));
        AddRule(new("とうた", "たった", ta, ta, id));
        AddRule(new("どうた", "だった", ta, ta, id));
        AddRule(new("のうた", "なった", ta, ta, id));
        AddRule(new("ほうた", "はった", ta, ta, id));
        AddRule(new("ぼうた", "ばった", ta, ta, id));
        AddRule(new("もうた", "まった", ta, ta, id));
        AddRule(new("ろうた", "らった", ta, ta, id));
        AddRule(new("ようた", "やった", ta, ta, id));
        AddRule(new("ゆうた", "いった", ta, ta, id));

        // kansai-ben -たら
        id = AddGroup(new TransformGroup("kansai-ben -たら", "-たら form of kansai-ben terms"));
        AddRule(new("うたら", "ったら", none, none, id));
        AddRule(new("おうたら", "あったら", none, none, id));
        AddRule(new("こうたら", "かったら", none, none, id));
        AddRule(new("ごうたら", "がったら", none, none, id));
        AddRule(new("そうたら", "さったら", none, none, id));
        AddRule(new("ぞうたら", "ざったら", none, none, id));
        AddRule(new("とうたら", "たったら", none, none, id));
        AddRule(new("どうたら", "だったら", none, none, id));
        AddRule(new("のうたら", "なったら", none, none, id));
        AddRule(new("ほうたら", "はったら", none, none, id));
        AddRule(new("ぼうたら", "ばったら", none, none, id));
        AddRule(new("もうたら", "まったら", none, none, id));
        AddRule(new("ろうたら", "らったら", none, none, id));
        AddRule(new("ようたら", "やったら", none, none, id));
        AddRule(new("ゆうたら", "いったら", none, none, id));

        // kansai-ben -たり
        id = AddGroup(new TransformGroup("kansai-ben -たり", "-たり form of kansai-ben terms"));
        AddRule(new("うたり", "ったり", none, none, id));
        AddRule(new("おうたり", "あったり", none, none, id));
        AddRule(new("こうたり", "かったり", none, none, id));
        AddRule(new("ごうたり", "がったり", none, none, id));
        AddRule(new("そうたり", "さったり", none, none, id));
        AddRule(new("ぞうたり", "ざったり", none, none, id));
        AddRule(new("とうたり", "たったり", none, none, id));
        AddRule(new("どうたり", "だったり", none, none, id));
        AddRule(new("のうたり", "なったり", none, none, id));
        AddRule(new("ほうたり", "はったり", none, none, id));
        AddRule(new("ぼうたり", "ばったり", none, none, id));
        AddRule(new("もうたり", "まったり", none, none, id));
        AddRule(new("ろうたり", "らったり", none, none, id));
        AddRule(new("ようたり", "やったり", none, none, id));
        AddRule(new("ゆうたり", "いったり", none, none, id));

        // kansai-ben -く
        id = AddGroup(new TransformGroup("kansai-ben -く", "-く stem of kansai-ben adjectives"));
        AddRule(new("う", "く", none, ku, id));
        AddRule(new("こう", "かく", none, ku, id));
        AddRule(new("ごう", "がく", none, ku, id));
        AddRule(new("そう", "さく", none, ku, id));
        AddRule(new("とう", "たく", none, ku, id));
        AddRule(new("のう", "なく", none, ku, id));
        AddRule(new("ぼう", "ばく", none, ku, id));
        AddRule(new("もう", "まく", none, ku, id));
        AddRule(new("ろう", "らく", none, ku, id));
        AddRule(new("よう", "よく", none, ku, id));
        AddRule(new("しゅう", "しく", none, ku, id));

        // kansai-ben adjective -て
        id = AddGroup(new TransformGroup("kansai-ben adjective -て", "-て form of kansai-ben adjectives"));
        AddRule(new("うて", "くて", te, te, id));
        AddRule(new("こうて", "かくて", te, te, id));
        AddRule(new("ごうて", "がくて", te, te, id));
        AddRule(new("そうて", "さくて", te, te, id));
        AddRule(new("とうて", "たくて", te, te, id));
        AddRule(new("のうて", "なくて", te, te, id));
        AddRule(new("ぼうて", "ばくて", te, te, id));
        AddRule(new("もうて", "まくて", te, te, id));
        AddRule(new("ろうて", "らくて", te, te, id));
        AddRule(new("ようて", "よくて", te, te, id));
        AddRule(new("しゅうて", "しくて", te, te, id));

        // kansai-ben adjective negative
        id = AddGroup(new TransformGroup("kansai-ben adjective negative", "Negative form of kansai-ben adjectives"));
        AddRule(new("うない", "くない", adjI, adjI, id));
        AddRule(new("こうない", "かくない", adjI, adjI, id));
        AddRule(new("ごうない", "がくない", adjI, adjI, id));
        AddRule(new("そうない", "さくない", adjI, adjI, id));
        AddRule(new("とうない", "たくない", adjI, adjI, id));
        AddRule(new("のうない", "なくない", adjI, adjI, id));
        AddRule(new("ぼうない", "ばくない", adjI, adjI, id));
        AddRule(new("もうない", "まくない", adjI, adjI, id));
        AddRule(new("ろうない", "らくない", adjI, adjI, id));
        AddRule(new("ようない", "よくない", adjI, adjI, id));
        AddRule(new("しゅうない", "しくない", adjI, adjI, id));

        // additional rules

        // -ましゅ
        id = AddGroup(new TransformGroup("-ましゅ", "Polite (childish).\nUsage: Replace ます with ましゅ."));
        AddRule(new("ましゅ", "ます", none, masu, id));

        // -ください
        id = AddGroup(new TransformGroup("-ください", "Polite request.\nUsage: Attach ください after the て-form of verbs."));
        AddRule(new("てください", "て", none, te, id));
        AddRule(new("でください", "で", none, te, id));

        // -くださる
        id = AddGroup(new TransformGroup("-くださる", "Do something for the speaker (respectful).\nUsage: Attach くださる after the て-form of verbs."));
        AddRule(new("てくださる", "て", v5, te, id));
        AddRule(new("でくださる", "で", v5, te, id));

        // -ごらん
        id = AddGroup(new TransformGroup("-ごらん", "Entice someone to try to do something.\nUsage: Attach ごらん after the て-form of verbs."));
        AddRule(new("てごらん", "て", none, te, id));
        AddRule(new("でごらん", "で", none, te, id));
        AddRule(new("てご覧", "て", none, te, id));
        AddRule(new("でご覧", "で", none, te, id));

        // -ごらんなさい
        id = AddGroup(new TransformGroup("-ごらんなさい", "Politely telling someone to try doing something.\nUsage: Attach ごらんなさい after the て-form of verbs."));
        AddRule(new("てごらんなさい", "て", none, te, id));
        AddRule(new("でごらんなさい", "で", none, te, id));
        AddRule(new("てご覧なさい", "て", none, te, id));
        AddRule(new("でご覧なさい", "で", none, te, id));

        // -いただく
        id = AddGroup(new TransformGroup("-いただく", "Receive the favor of someone doing (respectful).\nUsage: Attach いただく after the て-form of verbs."));
        AddRule(new("ていただく", "て", v5, te, id));
        AddRule(new("でいただく", "で", v5, te, id));

        // -あげる
        id = AddGroup(new TransformGroup("-あげる", "Do for someone.\nUsage: Attach あげる after the て-form of verbs."));
        AddRule(new("てあげる", "て", v1, te, id));
        AddRule(new("であげる", "で", v1, te, id));

        // -くれる
        id = AddGroup(new TransformGroup("-くれる", "Do for me/us.\nUsage: Attach くれる after the て-form of verbs."));
        AddRule(new("てくれる", "て", v1, te, id));
        AddRule(new("でくれる", "で", v1, te, id));

        // -もらう
        id = AddGroup(new TransformGroup("-もらう", "Receive the favour of someone doing.\nUsage: Attach もらう after the て-form of verbs."));
        AddRule(new("てもらう", "て", v5, te, id));
        AddRule(new("でもらう", "で", v5, te, id));

        // -やる
        id = AddGroup(new TransformGroup("-やる", "Do for someone (casual).\nUsage: Attach やる after the て-form of verbs."));
        AddRule(new("てやる", "て", v5, te, id));
        AddRule(new("でやる", "で", v5, te, id));

        // -さしあげる
        id = AddGroup(new TransformGroup("-さしあげる", "Do for someone (humble).\nUsage: Attach さしあげる after the て-form of verbs."));
        AddRule(new("てさしあげる", "て", v1, te, id));
        AddRule(new("でさしあげる", "で", v1, te, id));

        // -みる
        id = AddGroup(new TransformGroup("-みる", "Try to do something.\nUsage: Attach みる after the て-form of verbs."));
        AddRule(new("てみる", "て", v1, te, id));
        AddRule(new("でみる", "で", v1, te, id));

        // -みせる
        id = AddGroup(new TransformGroup("-みせる", "Showing of an action to someone.\nUsage: Attach みせる after the て-form of verbs."));
        AddRule(new("てみせる", "て", v1, te, id));
        AddRule(new("でみせる", "で", v1, te, id));

        // -ある
        id = AddGroup(new TransformGroup("-ある", "Resultant state (intentional).\nUsage: Attach ある after the て-form of verbs."));
        AddRule(new("てある", "て", v5, te, id));
        AddRule(new("である", "で", v5, te, id));

        // -いく
        id = AddGroup(new TransformGroup("-いく", "1. Action away from speaker.\n2. Indicates change continuing into the future.\nUsage: Attach いく after the て-form of verbs."));
        AddRule(new("ていく", "て", v5, te, id));
        AddRule(new("でいく", "で", v5, te, id));
        AddRule(new("てく", "て", none, te, id));
        AddRule(new("でく", "で", none, te, id));

        // -くる
        id = AddGroup(new TransformGroup("-くる", "1. Action towards speaker.\n2. Indicates ongoing change extending to present.\n3. Inception of a process.\nUsage: Attach くる after the て-form of verbs."));
        AddRule(new("てくる", "て", vk, te, id));
        AddRule(new("でくる", "で", vk, te, id));

        // -なさそう
        id = AddGroup(new TransformGroup("-なさそう", "Appearing not to be; does not seem like.\nUsage: Replace ない with なさそう."));
        AddRule(new("なさそう", "ない", none, adjI, id));

        // -ながら
        id = AddGroup(new TransformGroup("-ながら", "While doing something.\nUsage: Attach ながら after the continuative form (連用形) of verbs."));
        AddRule(new("ながら", "る", none, v1, id));
        AddRule(new("いながら", "う", none, v5, id));
        AddRule(new("きながら", "く", none, v5, id));
        AddRule(new("ぎながら", "ぐ", none, v5, id));
        AddRule(new("しながら", "す", none, v5, id));
        AddRule(new("ちながら", "つ", none, v5, id));
        AddRule(new("にながら", "ぬ", none, v5, id));
        AddRule(new("びながら", "ぶ", none, v5, id));
        AddRule(new("みながら", "む", none, v5, id));
        AddRule(new("りながら", "る", none, v5, id));
        AddRule(new("じながら", "ずる", none, vz, id));
        AddRule(new("しながら", "する", none, vs, id));
        AddRule(new("為ながら", "為る", none, vs, id));
        AddRule(new("きながら", "くる", none, vk, id));
        AddRule(new("来ながら", "来る", none, vk, id));
        AddRule(new("來ながら", "來る", none, vk, id));

        // -やがる
        id = AddGroup(new TransformGroup("-やがる", "Expresses the speakers contempt/anger towards someone else's action.\nUsage: Attach やがる after the continuative form (連用形) of verbs."));
        AddRule(new("やがる", "る", v5, v1, id));
        AddRule(new("いやがる", "う", v5, v5, id));
        AddRule(new("きやがる", "く", v5, v5, id));
        AddRule(new("ぎやがる", "ぐ", v5, v5, id));
        AddRule(new("しやがる", "す", v5, v5, id));
        AddRule(new("ちやがる", "つ", v5, v5, id));
        AddRule(new("にやがる", "ぬ", v5, v5, id));
        AddRule(new("びやがる", "ぶ", v5, v5, id));
        AddRule(new("みやがる", "む", v5, v5, id));
        AddRule(new("りやがる", "る", v5, v5, id));
        AddRule(new("じやがる", "ずる", v5, vz, id));
        AddRule(new("しやがる", "する", v5, vs, id));
        AddRule(new("為やがる", "為る", v5, vs, id));
        AddRule(new("きやがる", "くる", v5, vk, id));
        AddRule(new("来やがる", "来る", v5, vk, id));
        AddRule(new("來やがる", "來る", v5, vk, id));
    }

    private struct Rule
    {
        public string From;
        public string To;
        public JapaneseDeinflectionConditions ConditionsIn;
        public JapaneseDeinflectionConditions ConditionsOut;
        public int GroupId;

        public Rule(string from, string to, JapaneseDeinflectionConditions conditionsIn,
            JapaneseDeinflectionConditions conditionsOut, int groupId)
        {
            From = from;
            To = to;
            ConditionsIn = conditionsIn;
            ConditionsOut = conditionsOut;
            GroupId = groupId;
        }
    }

    private const string ShimauDescription =
        "1. Shows a sense of regret/surprise when you did have volition in doing something, but it turned out to be bad to do.\n2. Shows perfective/punctual achievement. This shows that an action has been completed.\n3. Shows unintentional action–\"accidentally\".\n";

    private const string PassiveDescription =
        "1. Indicates an action received from an action performer.\n2. Expresses respect for the subject of action performer.\n";
}
