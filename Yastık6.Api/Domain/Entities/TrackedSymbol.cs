namespace Yastık6.Api.Domain.Entities;

public sealed class TrackedSymbol
{
    public Guid Id { get; set; }
    
    // Yahoo formatındaki kod: Örn "THYAO.IS", "USDTRY=X", "BTC-USD"
    public string Code { get; set; } = null!; 
    
    // Kategori: "BIST", "FOREX", "CRYPTO" vb.
    public string SymbolType { get; set; } = null!; 
    
    // Pasife çekerek API isteklerini durdurabilmek için
    public bool IsActive { get; set; } = true; 
}