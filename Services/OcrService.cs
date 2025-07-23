using Tesseract;
using System.Text.RegularExpressions;

public class OcrService : IOcrService
{
    public async Task<InvoiceData> ExtractTextFromImageAsync(IFormFile file)
    {
        var tempPath = Path.GetTempFileName();

        using (var stream = new FileStream(tempPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        string extractedText = "";
        using (var engine = new TesseractEngine("./tessdata", "eng+nl", EngineMode.Default))
        using (var img = Pix.LoadFromFile(tempPath))
        using (var page = engine.Process(img))
        {
            extractedText = page.GetText();
        }

        var data = ParseInvoiceText(extractedText);
        return data;
    }

    private InvoiceData ParseInvoiceText(string text)
    {
        var data = new InvoiceData();

        // Voorbeelden van simpele regex extracties
        var dateMatch = Regex.Match(text, @"\b(\d{2}[\-/]\d{2}[\-/]\d{4})\b");
        var totalMatch = Regex.Match(text, @"(?i)Totaal\s*[:\-]?\s*\€?\s*(\d+[\.,]\d{2})");
        var invoiceNrMatch = Regex.Match(text, @"(?i)Factuurnummer\s*[:\-]?\s*(\S+)");

        if (dateMatch.Success) data.Date = dateMatch.Groups[1].Value;
        if (totalMatch.Success) data.TotalAmount = totalMatch.Groups[1].Value;
        if (invoiceNrMatch.Success) data.InvoiceNumber = invoiceNrMatch.Groups[1].Value;

        return data;
    }
}