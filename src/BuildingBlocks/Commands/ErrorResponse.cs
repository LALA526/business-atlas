namespace BuildingBlocks.Commands;

public sealed record ErrorResponse(
    string Code,
    string Message,
    string RequestId,
    string? TraceId = null)
{
    public static ErrorResponse From(Exception error, string requestId, string? traceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return error is AtlasCommandException commandError
            ? new ErrorResponse(commandError.Code, commandError.Message, requestId, traceId)
            : new ErrorResponse("INTERNAL_ERROR", "The command failed and all transactional writes were rolled back.", requestId, traceId);
    }
}
