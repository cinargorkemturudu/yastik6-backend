namespace Yastık6.Api.Application.Common.Interfaces;

public interface IPreviousCloseStore
{
    decimal? GetPreviousClose(string productCode);
    void SetAll(Dictionary<string, decimal> previousCloses);
}