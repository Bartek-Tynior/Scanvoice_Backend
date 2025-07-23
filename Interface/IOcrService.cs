public interface IOcrService
{
    Task<InvoiceData> ExtractTextFromImageAsync(IFormFile file);
}