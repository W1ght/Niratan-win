namespace Niratan.Models.DTO;

public sealed record NovelReaderWebMessage<TPayload>(
    int Version,
    string Type,
    TPayload Payload
);
