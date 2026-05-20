using System.Collections.Generic;

namespace Hoshi.Models.Dictionary;

public sealed record TransformGroup(string Name, string Description);

public sealed record Frequency(int Value, string DisplayValue);

public sealed record GlossaryEntry(
    string DictName,
    string Glossary,
    string DefinitionTags,
    string TermTags
);

public sealed record FrequencyEntry(string DictName, List<Frequency> Frequencies);

public sealed record PitchEntry(string DictName, List<int> PitchPositions);

public sealed record TermResult(
    string Expression,
    string Reading,
    string Rules,
    List<GlossaryEntry> Glossaries,
    List<FrequencyEntry> Frequencies,
    List<PitchEntry> Pitches
);

public sealed record DictionaryLookupResult(
    string Matched,
    string Deinflected,
    List<TransformGroup> Trace,
    TermResult Term,
    int PreprocessorSteps
);

public sealed record DictionaryStyle(string DictName, string Styles);

public sealed record ReaderSelectionData(
    string Text,
    string Sentence,
    double X,
    double Y,
    double Width,
    double Height,
    int? NormalizedOffset = null,
    int? SentenceOffset = null
);
