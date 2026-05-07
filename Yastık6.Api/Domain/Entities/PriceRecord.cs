namespace Yastık6.Api.Domain.Entities;

public sealed class PriceRecord
{
    public Guid Id { get; set; }
    
    public string ProductCode { get; set; } = null!; 
    
    public decimal AskPrice { get; set; } 
    public decimal BidPrice { get; set; } 
    
    public decimal? LastPrice { get; set; }
    
    public DateTimeOffset TimestampUtc { get; set; }
    
    public string Source { get; set; } = null!; 
}