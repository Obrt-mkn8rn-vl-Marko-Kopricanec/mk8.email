namespace mk8.email.Gateway.Protocols.Ews;

internal sealed class GatewayEwsRequestException(string code, string faultPrefix = "t",
    int status = StatusCodes.Status500InternalServerError, Exception? innerException = null)
    : Exception("The EWS request is not supported or is invalid.", innerException)
{
    public string Code { get; } = code;
    public string FaultPrefix { get; } = faultPrefix;
    public int Status { get; } = status;
    public bool IsHeaderFault { get; init; }

    public GatewayEwsRequestException() : this("ErrorInvalidRequest", "t", StatusCodes.Status500InternalServerError) { }
    public GatewayEwsRequestException(string code) : this(code, "t", StatusCodes.Status500InternalServerError) { }
    public GatewayEwsRequestException(string code, Exception innerException)
        : this(code, "t", StatusCodes.Status500InternalServerError, innerException) { }
}
