using System.Collections.Generic;

namespace Hoshi.Services.Dictionary;

public interface IDictionaryProfileContext
{
    string ActiveProfileId { get; }

    IReadOnlyList<string> ProfileIds { get; }

    string GetDictionaryConfigRoot(string profileId);

    bool EnableUnconfiguredDictionariesForProfile(string profileId);
}
