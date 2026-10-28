namespace GarageWatch.Api.Correlation;

public interface ICorrelationContext
{
    string CorrelationId { get; set; }
}

public sealed class CorrelationContext : ICorrelationContext
{
    public string CorrelationId { get; set; } = string.Empty;
}
