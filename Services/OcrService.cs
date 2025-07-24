using Tesseract;
using System.Text.RegularExpressions;
using System.Text.Json;

public class OcrService : IOcrService
{
    public async Task<InvoiceData> ExtractTextFromImageAsync(IFormFile file)
    {
        Console.WriteLine("Creating temporary file...");

        var tempPath = Path.GetTempFileName();

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Get the appropriate tessdata path based on the operating system
            string tessdataPath = GetTessdataPath();

            string extractedText = "";
            using (var engine = new TesseractEngine(tessdataPath, "eng+nld", EngineMode.Default))
            {
                // Configure OCR engine for better accuracy with invoices
                engine.SetVariable("tessedit_char_whitelist", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyzÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝàáâãäåçèéêëìíîïñòóôõöùúûüý€$.-/: +()");
                engine.SetVariable("preserve_interword_spaces", "1");
                engine.SetVariable("tessedit_pageseg_mode", "6"); // Assume uniform block of text
                engine.SetVariable("classify_bln_numeric_mode", "1");

                using (var img = Pix.LoadFromFile(tempPath))
                {
                    // Apply some basic image enhancement
                    using (var enhancedImg = img.ConvertRGBToGray(0.299f, 0.587f, 0.114f))
                    using (var page = engine.Process(enhancedImg))
                    {
                        extractedText = page.GetText();
                    }
                }
            }

            // Clean up the extracted text
            extractedText = CleanExtractedText(extractedText);

            var data = ParseInvoiceText(extractedText);

            Console.WriteLine("Extracted text: " + extractedText);
            Console.WriteLine("Data parsed: " + JsonSerializer.Serialize(data));

            return data;
        }
        finally
        {
            // Clean up temporary files
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error cleaning up temp files: {ex.Message}");
            }
        }
    }

    private string CleanExtractedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Remove excessive whitespace and clean up common OCR errors
        text = Regex.Replace(text, @"\s+", " ");
        text = Regex.Replace(text, @"[^\w\s€$.,:\-/+()]", " ");
        text = text.Replace("€", "EUR");
        text = text.Replace("$", "USD");

        // Fix common OCR character misidentifications
        text = text.Replace("0", "O").Replace("O", "0"); // This might seem redundant but can help
        text = text.Replace("l", "1").Replace("I", "1");
        text = text.Replace("S", "5").Replace("5", "S");

        return text.Trim();
    }

    private string GetTessdataPath()
    {
        // Check if tessdata environment variable is set
        var tessdataEnv = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrEmpty(tessdataEnv))
        {
            return tessdataEnv;
        }

        // Check for common tessdata locations based on OS
        if (OperatingSystem.IsMacOS())
        {
            // Homebrew installation path on macOS
            if (Directory.Exists("/opt/homebrew/share/tessdata"))
                return "/opt/homebrew/share/tessdata";

            // Intel Mac Homebrew path
            if (Directory.Exists("/usr/local/share/tessdata"))
                return "/usr/local/share/tessdata";
        }
        else if (OperatingSystem.IsLinux())
        {
            // Common Linux paths
            if (Directory.Exists("/usr/share/tessdata"))
                return "/usr/share/tessdata";
            if (Directory.Exists("/usr/local/share/tessdata"))
                return "/usr/local/share/tessdata";
        }
        else if (OperatingSystem.IsWindows())
        {
            // Windows default path - relative to the executable
            return "./tessdata";
        }

        // Fallback to relative path
        return "./tessdata";
    }

    private InvoiceData ParseInvoiceText(string text)
    {
        var data = new InvoiceData();

        if (string.IsNullOrEmpty(text)) return data;

        Console.WriteLine("Parsing text: " + text);

        // More flexible date patterns
        var datePatterns = new[]
        {
            @"\b(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})\b",  // dd/mm/yyyy or dd-mm-yyyy
            @"\b(\d{4}[-/.]\d{1,2}[-/.]\d{1,2})\b",  // yyyy/mm/dd or yyyy-mm-dd
            @"\b(\d{1,2}\s+\w+\s+\d{4})\b"          // dd month yyyy
        };

        foreach (var pattern in datePatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                data.Date = match.Groups[1].Value;
                break;
            }
        }

        // More flexible total amount patterns
        var totalPatterns = new[]
        {
            @"(?i)totaal\s*[:\-]?\s*(?:EUR|€)?\s*(\d+[.,]\d{2})",
            @"(?i)total\s*[:\-]?\s*(?:EUR|€)?\s*(\d+[.,]\d{2})",
            @"(?i)bedrag\s*[:\-]?\s*(?:EUR|€)?\s*(\d+[.,]\d{2})",
            @"(?:EUR|€)\s*(\d+[.,]\d{2})",
            @"\b(\d+[.,]\d{2})\s*(?:EUR|€)",
            @"(\d+[.,]\d{2})\s*$"  // Amount at end of line
        };

        foreach (var pattern in totalPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (match.Success)
            {
                data.TotalAmount = match.Groups[1].Value;
                break;
            }
        }

        // More flexible invoice number patterns
        var invoicePatterns = new[]
        {
            @"(?i)factuurnummer\s*[:\-]?\s*(\w+)",
            @"(?i)factuur\s*[:\-]?\s*nr\s*[:\-]?\s*(\w+)",
            @"(?i)invoice\s*[:\-]?\s*(?:number|nr|no)\s*[:\-]?\s*(\w+)",
            @"(?i)inv\s*[:\-]?\s*(?:nr|no)\s*[:\-]?\s*(\w+)",
            @"(?i)nr\s*[:\-]?\s*(\d+)",
            @"\b(INV\d+)\b",
            @"\b(\d{6,})\b"  // Long number that could be invoice number
        };

        foreach (var pattern in invoicePatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                data.InvoiceNumber = match.Groups[1].Value;
                break;
            }
        }

        return data;
    }
}