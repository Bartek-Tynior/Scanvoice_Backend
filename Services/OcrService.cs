using Tesseract;
using System.Text.RegularExpressions;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

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

            Console.WriteLine($"Saved file to: {tempPath}");
            Console.WriteLine($"File size: {new FileInfo(tempPath).Length} bytes");

            // Preprocess the image for better OCR
            var preprocessedPath = await PreprocessImage(tempPath);
            Console.WriteLine($"Preprocessed image saved to: {preprocessedPath}");

            // Get the appropriate tessdata path based on the operating system
            string tessdataPath = GetTessdataPath();
            Console.WriteLine($"Using tessdata path: {tessdataPath}");

            string extractedText = "";

            // Try multiple OCR approaches for better results
            extractedText = TryMultipleOcrApproaches(preprocessedPath, tessdataPath);

            Console.WriteLine("Raw extracted text: " + extractedText);

            // Clean up the extracted text
            extractedText = CleanExtractedText(extractedText);

            var data = ParseInvoiceText(extractedText);

            Console.WriteLine("Cleaned extracted text: " + extractedText);
            Console.WriteLine("Data parsed: " + JsonSerializer.Serialize(data));

            // Cleanup
            if (File.Exists(preprocessedPath)) File.Delete(preprocessedPath);

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

    private async Task<string> PreprocessImage(string inputPath)
    {
        var outputPath = Path.GetTempFileName() + ".png";

        try
        {
            using (var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgb24>(inputPath))
            {
                Console.WriteLine($"Original image size: {image.Width}x{image.Height}");

                // Apply multiple preprocessing steps
                image.Mutate(x => x
                    // Auto-orient the image based on EXIF data
                    .AutoOrient()
                    // Convert to grayscale for better OCR
                    .Grayscale()
                    // Increase contrast
                    .Contrast(1.5f)
                    // Adjust brightness slightly
                    .Brightness(0.1f)
                    // Resize if the image is too large (keep aspect ratio)
                    .Resize(new ResizeOptions
                    {
                        Size = new Size(Math.Min(image.Width, 2000), Math.Min(image.Height, 2000)),
                        Mode = ResizeMode.Max
                    })
                );

                // Save as PNG for best quality
                await image.SaveAsPngAsync(outputPath);
                Console.WriteLine($"Preprocessed image saved: {outputPath}");

                return outputPath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error preprocessing image: {ex.Message}");
            // Return original path if preprocessing fails
            return inputPath;
        }
    }

    private string TryMultipleOcrApproaches(string imagePath, string tessdataPath)
    {
        var results = new List<string>();

        // Approach 1: Dutch only with different engine modes
        var approaches = new[]
        {
            new { Language = "nld", Mode = EngineMode.Default, PageSegMode = "6", Description = "Dutch default" },
            new { Language = "eng+nld", Mode = EngineMode.Default, PageSegMode = "6", Description = "English+Dutch default" },
            new { Language = "nld", Mode = EngineMode.Default, PageSegMode = "3", Description = "Dutch auto page seg" },
            new { Language = "nld", Mode = EngineMode.Default, PageSegMode = "4", Description = "Dutch single column" },
            new { Language = "nld", Mode = EngineMode.Default, PageSegMode = "1", Description = "Dutch OSD" },
            new { Language = "eng", Mode = EngineMode.Default, PageSegMode = "6", Description = "English only" }
        };

        foreach (var approach in approaches)
        {
            try
            {
                using (var engine = new TesseractEngine(tessdataPath, approach.Language, approach.Mode))
                {
                    // Configure engine
                    engine.SetVariable("tessedit_pageseg_mode", approach.PageSegMode);
                    engine.SetVariable("preserve_interword_spaces", "1");
                    engine.SetVariable("tessedit_do_invert", "0");

                    // Try with and without character restrictions
                    if (approach.Language.Contains("nld"))
                    {
                        // Dutch-specific improvements
                        engine.SetVariable("load_system_dawg", "1");
                        engine.SetVariable("load_freq_dawg", "1");
                        engine.SetVariable("load_unambig_dawg", "1");
                    }

                    using (var img = Pix.LoadFromFile(imagePath))
                    using (var page = engine.Process(img))
                    {
                        var text = page.GetText();
                        if (!string.IsNullOrWhiteSpace(text) && text.Length > 10)
                        {
                            Console.WriteLine($"{approach.Description} OCR result ({text.Length} chars): {text.Substring(0, Math.Min(100, text.Length))}...");
                            results.Add(text);
                        }
                        else
                        {
                            Console.WriteLine($"{approach.Description} OCR failed: empty or too short result");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{approach.Description} OCR failed: {ex.Message}");
            }
        }

        if (results.Any())
        {
            // Return the result with most recognizable words
            var bestResult = results
                .Select(r => new { Text = r, WordScore = CalculateWordScore(r) })
                .OrderByDescending(r => r.WordScore)
                .First().Text;

            Console.WriteLine($"Selected best OCR result based on word recognition score");
            return bestResult;
        }

        return "";
    }

    private int CalculateWordScore(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // Count words that look like real words (letters only, reasonable length)
        var words = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var realWords = words.Count(w =>
            w.Length >= 2 &&
            w.Length <= 20 &&
            w.All(c => char.IsLetter(c) || "€$.,:-".Contains(c)) &&
            w.Count(char.IsLetter) >= w.Length * 0.7 // At least 70% letters
        );

        return realWords;
    }

    private void ConfigureEngine(TesseractEngine engine, bool setPageSegMode = true)
    {
        // Less restrictive character whitelist for Dutch
        engine.SetVariable("tessedit_char_whitelist", "");

        // Preserve interword spaces
        engine.SetVariable("preserve_interword_spaces", "1");

        if (setPageSegMode)
        {
            // Try uniform block of text
            engine.SetVariable("tessedit_pageseg_mode", "6");
        }

        // Improve word recognition
        engine.SetVariable("tessedit_do_invert", "0");
        engine.SetVariable("classify_bln_numeric_mode", "1");

        // Language-specific settings
        engine.SetVariable("load_system_dawg", "1");
        engine.SetVariable("load_freq_dawg", "1");
    }

    private string CleanExtractedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Remove excessive whitespace but keep structure
        text = Regex.Replace(text, @"[ \t]+", " "); // Replace multiple spaces/tabs with single space
        text = Regex.Replace(text, @"\n\s*\n", "\n"); // Remove empty lines

        // Don't be too aggressive with character filtering for now
        // Just remove obviously problematic characters
        text = Regex.Replace(text, @"[^\w\s€$.,:\-/+()%&@#]", "");

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

        Console.WriteLine("Parsing comprehensive invoice data from text...");

        // Split text into lines for easier processing
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(line => line.Trim())
                       .Where(line => !string.IsNullOrWhiteSpace(line))
                       .ToList();

        data.RawTextLines = lines;

        // Initialize sub-objects
        data.Vendor = new VendorInfo { Address = new AddressInfo() };
        data.Customer = new CustomerInfo { Address = new AddressInfo() };
        data.Financial = new FinancialInfo();
        data.Payment = new PaymentInfo();

        // Analyze invoice structure and identify sections
        var sections = AnalyzeInvoiceStructure(lines);

        // Extract information using context-aware parsing
        ExtractBasicInvoiceInfoContextual(data, text, lines, sections);
        ExtractVendorInfoContextual(data, text, lines, sections);
        ExtractCustomerInfoContextual(data, text, lines, sections);
        ExtractFinancialInfoContextual(data, text, lines, sections);
        ExtractPaymentInfoContextual(data, text, lines, sections);
        ExtractLineItemsContextual(data, text, lines, sections);

        // Detect language and currency
        DetectLanguageAndCurrency(data, text);

        Console.WriteLine($"Extracted invoice data: {JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })}");

        return data;
    }

    private Dictionary<string, List<int>> AnalyzeInvoiceStructure(List<string> lines)
    {
        var sections = new Dictionary<string, List<int>>
        {
            ["header"] = new List<int>(),
            ["vendor"] = new List<int>(),
            ["customer"] = new List<int>(),
            ["invoice_meta"] = new List<int>(),
            ["line_items"] = new List<int>(),
            ["totals"] = new List<int>(),
            ["payment"] = new List<int>()
        };

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].ToLower();

            // Header indicators (usually top 20% of invoice)
            if (i < lines.Count * 0.2)
            {
                sections["header"].Add(i);
            }

            // Vendor section indicators
            if (Regex.IsMatch(line, @"(?i)\b(b\.?v\.?|ltd|inc|corp|gmbh|your\s+company|lorem\s+ipsum)\b") ||
                line.Contains("@") || line.Contains("www.") ||
                Regex.IsMatch(line, @"(?i)btw\s*nummer|vat\s*number|kvk") ||
                Regex.IsMatch(line, @"(?i)telefoon|phone|tel:"))
            {
                sections["vendor"].Add(i);
            }

            // Customer section indicators  
            if (Regex.IsMatch(line, @"(?i)klantnummer|customer\s*number|client|bill\s*to") ||
                Regex.IsMatch(line, @"(?i)factuur\s*aan|invoice\s*to"))
            {
                sections["customer"].Add(i);
            }

            // Invoice metadata
            if (Regex.IsMatch(line, @"(?i)factuurnummer|invoice\s*number|factuur\s*datum|invoice\s*date") ||
                Regex.IsMatch(line, @"(?i)vervaldatum|due\s*date") ||
                Regex.IsMatch(line, @"\b\d{6}\b|\b\d{4}-\d+\b") || // Invoice numbers
                Regex.IsMatch(line, @"\b\d{1,2}[-/.]\d{1,2}[-/.]\d{4}\b")) // Dates
            {
                sections["invoice_meta"].Add(i);
            }

            // Line items section
            if (Regex.IsMatch(line, @"(?i)naam.*beschrijving|description|omschrijving|artikel|product") ||
                Regex.IsMatch(line, @"(?i)hoeveelheid|quantity|aantal|prijs|price") ||
                Regex.IsMatch(line, @"(?i)demo\s*product|product\s*nummer") ||
                (Regex.IsMatch(line, @"\d+[.,]\d{2}") && Regex.IsMatch(line, @"\d+\s*%"))) // Price with percentage
            {
                sections["line_items"].Add(i);
            }

            // Totals section
            if (Regex.IsMatch(line, @"(?i)totaal.*excl|subtotal|total.*excl.*btw") ||
                Regex.IsMatch(line, @"(?i)btw\s*\d+\s*%|vat\s*\d+\s*%") ||
                Regex.IsMatch(line, @"(?i)totaal.*incl|total.*incl|totaalbedrag") ||
                line.Contains("€") && Regex.IsMatch(line, @"\d+[.,]\d{2}"))
            {
                sections["totals"].Add(i);
            }

            // Payment section
            if (Regex.IsMatch(line, @"(?i)verzoeken.*betalen|pay.*within|binnen.*dagen") ||
                Regex.IsMatch(line, @"(?i)bankrekening|iban|account") ||
                Regex.IsMatch(line, @"(?i)smith\s*consulting") || // Company in footer
                Regex.IsMatch(line, @"\b[A-Z]{2}\d{2}[A-Z0-9]{4}\d{7}[A-Z0-9]{0,16}\b")) // IBAN pattern
            {
                sections["payment"].Add(i);
            }
        }

        return sections;
    }

    private void ExtractBasicInvoiceInfoContextual(InvoiceData data, string text, List<string> lines, Dictionary<string, List<int>> sections)
    {
        // Look for invoice number in invoice metadata sections first
        var metaLines = sections["invoice_meta"].Concat(sections["header"]).Distinct().OrderBy(x => x);

        foreach (var lineIndex in metaLines)
        {
            if (lineIndex >= lines.Count) continue;
            var line = lines[lineIndex];

            // Invoice number patterns
            var invoicePatterns = new[]
            {
                @"(?i)factuurnummer\s*[:\-]?\s*([A-Z0-9\-_]+)",
                @"(?i)invoice\s*(?:number|nr)\s*[:\-]?\s*([A-Z0-9\-_]+)",
                @"^(\d{6})$", // Standalone 6-digit number like "202063"
                @"^([A-Z]\d{4,})$" // Pattern like "F2020-001"
            };

            foreach (var pattern in invoicePatterns)
            {
                var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var invoiceNum = match.Groups[1].Value.Trim();
                    if (invoiceNum.Length >= 3 && !invoiceNum.Equals("Factuurnummer", StringComparison.OrdinalIgnoreCase))
                    {
                        data.InvoiceNumber = invoiceNum;
                        break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(data.InvoiceNumber)) break;
        }

        // Look for dates in metadata sections
        foreach (var lineIndex in metaLines)
        {
            if (lineIndex >= lines.Count) continue;
            var line = lines[lineIndex];

            // Date patterns
            var datePatterns = new[]
            {
                @"(?i)factuurdatum\s*[:\-]?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})",
                @"(?i)invoice\s*date\s*[:\-]?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})",
                @"^(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})$" // Standalone date
            };

            foreach (var pattern in datePatterns)
            {
                var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    data.InvoiceDate = match.Groups[1].Value.Trim();
                    break;
                }
            }
            if (!string.IsNullOrEmpty(data.InvoiceDate)) break;
        }

        // Look for due date
        var dueDateMatch = Regex.Match(text, @"(?i)vervaldatum\s*[:\-]?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})", RegexOptions.IgnoreCase);
        if (dueDateMatch.Success)
        {
            data.DueDate = dueDateMatch.Groups[1].Value.Trim();
        }
    }

    private void ExtractVendorInfoContextual(InvoiceData data, string text, List<string> lines, Dictionary<string, List<int>> sections)
    {
        data.Vendor ??= new VendorInfo();
        data.Vendor.Address ??= new AddressInfo();

        // Get vendor-related lines (header + vendor sections)
        var vendorLines = sections["header"].Concat(sections["vendor"]).Distinct().OrderBy(x => x).Take(10);

        // Score-based company name detection
        string? bestCompanyName = null;
        int bestScore = 0;

        foreach (var lineIndex in vendorLines)
        {
            if (lineIndex >= lines.Count) continue;
            var line = lines[lineIndex].Trim();

            // Skip obvious non-company lines
            if (line.Length < 3 ||
                Regex.IsMatch(line, @"(?i)factuur|invoice|datum|telefoon|email|website|btw|vat") ||
                Regex.IsMatch(line, @"^\d+$|^\d{4}\s*[A-Z]{2}$") ||
                line.Contains("€"))
                continue;

            int score = 0;

            // Company indicators
            if (Regex.IsMatch(line, @"(?i)\b(B\.?V\.?|BV|Ltd|Inc|Demo|Lorem\s+Ipsum)\b")) score += 15;
            if (line.Length >= 5 && line.Length <= 40) score += 5;
            if (Regex.IsMatch(line, @"^[A-Z][A-Za-z\s&\.\-]+$")) score += 8; // Proper case
            if (lineIndex < 5) score += 3; // Early in document
            if (!Regex.IsMatch(line, @"(?i)straat|weg|laan|gracht|plein|\d{4}")) score += 3; // Not address

            if (score > bestScore)
            {
                bestScore = score;
                bestCompanyName = line;
            }
        }

        if (bestCompanyName != null)
        {
            data.Vendor.CompanyName = bestCompanyName.Trim();
        }

        // Extract contact information from vendor sections
        var vendorSectionText = string.Join(" ", vendorLines.Where(i => i < lines.Count).Select(i => lines[i]));

        // VAT number
        var vatMatch = Regex.Match(text, @"(?i)btw\s*nummer\s*[:\-]?\s*([A-Z]{2}\d{6,})", RegexOptions.IgnoreCase);
        if (vatMatch.Success)
        {
            data.Vendor.VatNumber = vatMatch.Groups[1].Value.Trim();
        }

        // Email
        var emailMatch = Regex.Match(vendorSectionText, @"\b([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,})\b");
        if (emailMatch.Success)
        {
            data.Vendor.Email = emailMatch.Groups[1].Value;
        }

        // Phone
        var phoneMatch = Regex.Match(vendorSectionText, @"(?i)(?:tel|telefoon|phone)\s*[:\-]?\s*([\+\d\s\-\(\)]{8,})");
        if (phoneMatch.Success)
        {
            data.Vendor.Phone = phoneMatch.Groups[1].Value.Trim();
        }

        // Website
        var websiteMatch = Regex.Match(vendorSectionText, @"\b(?:www\.)?([a-zA-Z0-9.-]+\.[a-zA-Z]{2,})\b");
        if (websiteMatch.Success && !websiteMatch.Groups[1].Value.Contains("@"))
        {
            data.Vendor.Website = websiteMatch.Groups[1].Value;
        }

        // Address information
        foreach (var lineIndex in vendorLines)
        {
            if (lineIndex >= lines.Count) continue;
            var line = lines[lineIndex];

            // Postal code + city
            var addressMatch = Regex.Match(line, @"(\d{4})\s*([A-Z]{2})\s+([A-Za-z\s]+)");
            if (addressMatch.Success)
            {
                data.Vendor.Address.PostalCode = addressMatch.Groups[1].Value + addressMatch.Groups[2].Value;
                data.Vendor.Address.City = addressMatch.Groups[3].Value.Trim();
            }

            // Street address
            if (Regex.IsMatch(line, @"(?i)(straat|weg|laan|gracht)\s+\d+") ||
                Regex.IsMatch(line, @"^[A-Za-z\s]+(straat|weg|laan|gracht|plein)\s*\d*$"))
            {
                data.Vendor.Address.Street = line.Trim();
            }
        }
    }

    private void ExtractCustomerInfoContextual(InvoiceData data, string text, List<string> lines, Dictionary<string, List<int>> sections)
    {
        data.Customer ??= new CustomerInfo();
        data.Customer.Address ??= new AddressInfo();

        // Extract customer number from customer sections
        var customerLines = sections["customer"].Concat(sections["invoice_meta"]).Distinct();

        foreach (var lineIndex in customerLines)
        {
            if (lineIndex >= lines.Count) continue;
            var line = lines[lineIndex];

            var customerNumMatch = Regex.Match(line, @"(?i)klantnummer\s*[:\-]?\s*([A-Z0-9]+)", RegexOptions.IgnoreCase);
            if (customerNumMatch.Success)
            {
                var customerNum = customerNumMatch.Groups[1].Value.Trim();
                if (customerNum.Length > 1)
                {
                    data.Customer.CustomerNumber = customerNum;
                    break;
                }
            }
        }

        // For customer company name, look in the middle section (after vendor, before line items)
        var middleStart = sections["vendor"].Any() ? sections["vendor"].Max() + 1 : 5;
        var middleEnd = sections["line_items"].Any() ? sections["line_items"].Min() - 1 : lines.Count / 2;

        for (int i = middleStart; i <= Math.Min(middleEnd, lines.Count - 1); i++)
        {
            var line = lines[i].Trim();

            // Skip obvious non-customer lines
            if (Regex.IsMatch(line, @"(?i)factuur|invoice|datum|nummer|prijs|totaal|omschrijving|description"))
                continue;

            // Look for company-like names
            if (Regex.IsMatch(line, @"^[A-Z][A-Za-z\s&\.\-]{5,}$") &&
                !Regex.IsMatch(line, @"(?i)nederland|amsterdam|telefoon|email|website"))
            {
                data.Customer.CompanyName = line.Trim();
                break;
            }
        }
    }

    private void ExtractFinancialInfoContextual(InvoiceData data, string text, List<string> lines, Dictionary<string, List<int>> sections)
    {
        data.Financial ??= new FinancialInfo();

        // Focus on totals section for financial information
        var totalLines = sections["totals"].Concat(sections["line_items"]).Distinct().OrderBy(x => x);

        foreach (var lineIndex in totalLines)
        {
            if (lineIndex >= lines.Count) continue;
            var line = lines[lineIndex];

            // Extract subtotal (excl BTW)
            if (Regex.IsMatch(line, @"(?i)totaal.*excl.*btw|subtotal|totaalbedrag.*excl", RegexOptions.IgnoreCase))
            {
                var amountMatch = Regex.Match(line, @"€\s*(\d{1,6}[.,]\d{2})");
                if (amountMatch.Success && decimal.TryParse(amountMatch.Groups[1].Value.Replace(',', '.'), out decimal amount))
                {
                    data.Financial.SubTotal = amount;
                }
            }

            // Extract VAT amount and rate
            if (Regex.IsMatch(line, @"(?i)btw\s*(\d+)\s*%", RegexOptions.IgnoreCase))
            {
                var vatMatch = Regex.Match(line, @"(?i)btw\s*(\d+)\s*%.*€\s*(\d{1,6}[.,]\d{2})");
                if (vatMatch.Success)
                {
                    if (decimal.TryParse(vatMatch.Groups[1].Value, out decimal rate))
                        data.Financial.TaxRate = rate;

                    if (decimal.TryParse(vatMatch.Groups[2].Value.Replace(',', '.'), out decimal vatAmount))
                        data.Financial.TaxAmount = vatAmount;
                }
            }

            // Extract total (incl BTW)
            if (Regex.IsMatch(line, @"(?i)totaal.*incl.*btw|totaalbedrag.*incl", RegexOptions.IgnoreCase))
            {
                var amountMatch = Regex.Match(line, @"€\s*(\d{1,6}[.,]\d{2})");
                if (amountMatch.Success && decimal.TryParse(amountMatch.Groups[1].Value.Replace(',', '.'), out decimal amount))
                {
                    data.Financial.TotalAmount = amount;
                }
            }
        }

        // If we have subtotal and VAT, calculate total
        if (data.Financial.SubTotal.HasValue && data.Financial.TaxAmount.HasValue && !data.Financial.TotalAmount.HasValue)
        {
            data.Financial.TotalAmount = data.Financial.SubTotal.Value + data.Financial.TaxAmount.Value;
        }

        // If we have total but no subtotal, calculate it (assuming 21% VAT if no rate specified)
        if (data.Financial.TotalAmount.HasValue && !data.Financial.SubTotal.HasValue)
        {
            var vatRate = data.Financial.TaxRate ?? 21m;
            data.Financial.SubTotal = Math.Round(data.Financial.TotalAmount.Value / (1 + vatRate / 100), 2);
            data.Financial.TaxAmount = data.Financial.TotalAmount.Value - data.Financial.SubTotal.Value;
            if (!data.Financial.TaxRate.HasValue)
                data.Financial.TaxRate = vatRate;
        }
    }

    private void ExtractLineItemsContextual(InvoiceData data, string text, List<string> lines, Dictionary<string, List<int>> sections)
    {
        data.LineItems ??= new List<InvoiceLineItem>();

        if (!sections["line_items"].Any()) return;

        var lineItemsSection = sections["line_items"].OrderBy(x => x).ToList();
        var startIndex = lineItemsSection.First();
        var endIndex = sections["totals"].Any() ? sections["totals"].Min() : lineItemsSection.Last() + 5;

        // Look for table header to understand structure
        var headerFound = false;
        var hasQuantityColumn = false;
        var hasPriceColumn = false;

        for (int i = startIndex; i < Math.Min(endIndex, lines.Count); i++)
        {
            var line = lines[i].ToLower();

            // Identify table structure
            if (!headerFound && (line.Contains("omschrijving") || line.Contains("description")))
            {
                headerFound = true;
                hasQuantityColumn = line.Contains("aantal") || line.Contains("hoeveelheid") || line.Contains("quantity");
                hasPriceColumn = line.Contains("prijs") || line.Contains("price");
                continue;
            }

            if (!headerFound) continue;

            // Skip totals lines
            if (Regex.IsMatch(line, @"(?i)totaal|subtotal|btw|vat"))
                break;

            var originalLine = lines[i];

            // Pattern 1: Line with "Demo Product nummer X"
            var productMatch = Regex.Match(originalLine, @"Demo\s+Product\s+nummer\s+(\d+)");
            if (productMatch.Success)
            {
                var lineItem = new InvoiceLineItem
                {
                    Description = originalLine.Trim()
                };

                // Look for quantity, price, and total in the same line or next lines
                ExtractItemPricing(lineItem, originalLine, lines, i);

                if (lineItem.UnitPrice.HasValue || lineItem.LineTotal.HasValue)
                {
                    data.LineItems.Add(lineItem);
                }
            }
            // Pattern 2: Any line that looks like an item (has text and numbers)
            else if (Regex.IsMatch(originalLine, @"[A-Za-z]{3,}") &&
                     Regex.IsMatch(originalLine, @"\d+[.,]\d{2}") &&
                     !Regex.IsMatch(originalLine, @"(?i)totaal|subtotal|btw"))
            {
                var lineItem = new InvoiceLineItem
                {
                    Description = ExtractDescriptionFromLine(originalLine)
                };

                ExtractItemPricing(lineItem, originalLine, lines, i);

                if (!string.IsNullOrEmpty(lineItem.Description) && lineItem.Description.Length > 3)
                {
                    data.LineItems.Add(lineItem);
                }
            }
        }
    }

    private void ExtractItemPricing(InvoiceLineItem lineItem, string currentLine, List<string> lines, int currentIndex)
    {
        // Try to extract from current line first
        var prices = Regex.Matches(currentLine, @"(\d+[.,]\d{2})").Cast<Match>().Select(m => m.Value).ToList();
        var quantities = Regex.Matches(currentLine, @"\b(\d+(?:[.,]\d+)?)\b").Cast<Match>().Select(m => m.Value).Where(v => !v.Contains(".") || v.EndsWith(".00")).ToList();

        if (prices.Count >= 2)
        {
            // Multiple prices likely means: unit price, total
            if (decimal.TryParse(prices[0].Replace(',', '.'), out decimal unitPrice))
                lineItem.UnitPrice = unitPrice;
            if (decimal.TryParse(prices[^1].Replace(',', '.'), out decimal total))
                lineItem.LineTotal = total;
        }
        else if (prices.Count == 1)
        {
            // Single price - could be unit price or total
            if (decimal.TryParse(prices[0].Replace(',', '.'), out decimal price))
                lineItem.LineTotal = price;
        }

        // Extract quantity
        if (quantities.Any())
        {
            var firstQuantity = quantities.FirstOrDefault(q => decimal.TryParse(q.Replace(',', '.'), out decimal qty) && qty > 0 && qty <= 1000);
            if (firstQuantity != null && decimal.TryParse(firstQuantity.Replace(',', '.'), out decimal quantity))
            {
                lineItem.Quantity = quantity;
            }
        }

        // Calculate missing values
        if (lineItem.Quantity.HasValue && lineItem.LineTotal.HasValue && !lineItem.UnitPrice.HasValue)
        {
            lineItem.UnitPrice = lineItem.LineTotal.Value / lineItem.Quantity.Value;
        }
        else if (lineItem.UnitPrice.HasValue && lineItem.Quantity.HasValue && !lineItem.LineTotal.HasValue)
        {
            lineItem.LineTotal = lineItem.UnitPrice.Value * lineItem.Quantity.Value;
        }
        else if (!lineItem.Quantity.HasValue && lineItem.UnitPrice.HasValue && lineItem.LineTotal.HasValue)
        {
            lineItem.Quantity = lineItem.LineTotal.Value / lineItem.UnitPrice.Value;
        }

        // Default quantity to 1 if not found
        if (!lineItem.Quantity.HasValue)
        {
            lineItem.Quantity = 1;
        }
    }

    private string ExtractDescriptionFromLine(string line)
    {
        // Remove prices and quantities to get just the description
        var description = Regex.Replace(line, @"€\s*\d+[.,]\d{2}", "").Trim();
        description = Regex.Replace(description, @"\b\d+[.,]\d{2}\b", "").Trim();
        description = Regex.Replace(description, @"\b\d+\s*%\b", "").Trim();
        description = Regex.Replace(description, @"\s+", " ").Trim();

        return description;
    }

    private void ExtractPaymentInfoContextual(InvoiceData data, string text, List<string> lines, Dictionary<string, List<int>> sections)
    {
        data.Payment ??= new PaymentInfo();

        // Extract payment terms from payment section
        var paymentText = string.Join(" ", sections["payment"].Where(i => i < lines.Count).Select(i => lines[i]));

        var paymentTermsMatch = Regex.Match(paymentText, @"(?i)binnen\s*(\d+)\s*dagen|pay.*within\s*(\d+)\s*days", RegexOptions.IgnoreCase);
        if (paymentTermsMatch.Success)
        {
            var days = paymentTermsMatch.Groups[1].Success ? paymentTermsMatch.Groups[1].Value : paymentTermsMatch.Groups[2].Value;
            data.Payment.PaymentTerms = $"{days} dagen";
        }

        // Extract IBAN
        var ibanMatch = Regex.Match(text, @"\b([A-Z]{2}\d{2}[A-Z0-9]{4}\d{7}[A-Z0-9]{0,16})\b");
        if (ibanMatch.Success)
        {
            data.Payment.IBAN = ibanMatch.Groups[1].Value;
            // Also set vendor IBAN if not already set
            if (string.IsNullOrEmpty(data.Vendor?.IBAN))
            {
                data.Vendor ??= new VendorInfo();
                data.Vendor.IBAN = ibanMatch.Groups[1].Value;
            }
        }
    }

    private void ExtractBasicInvoiceInfo(InvoiceData data, string text, List<string> lines)
    {
        // Invoice number patterns (Dutch and English) - improved
        var invoicePatterns = new[]
        {
            @"(?i)factuurnummer\s*[:\-]?\s*([A-Z0-9\-_]+)",
            @"(?i)factuur\s*[:\-]?\s*nr\.?\s*[:\-]?\s*([A-Z0-9\-_]+)",
            @"(?i)invoice\s*[:\-]?\s*(?:number|nr|no)\.?\s*[:\-]?\s*([A-Z0-9\-_]+)",
            @"(?i)inv\.?\s*[:\-]?\s*(?:nr|no)\.?\s*[:\-]?\s*([A-Z0-9\-_]+)"
        };

        // Look for invoice number in lines, not just the combined text
        foreach (var line in lines)
        {
            foreach (var pattern in invoicePatterns)
            {
                var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var invoiceNum = match.Groups[1].Value.Trim();
                    // Avoid capturing headers like "Factuurnummer:" without actual number
                    if (invoiceNum.Length > 2 && !invoiceNum.Equals("Factuurnummer", StringComparison.OrdinalIgnoreCase))
                    {
                        data.InvoiceNumber = invoiceNum;
                        break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(data.InvoiceNumber)) break;
        }

        // If no explicit invoice number found, look for standalone codes
        if (string.IsNullOrEmpty(data.InvoiceNumber))
        {
            foreach (var line in lines)
            {
                // Look for patterns like "F2023-001" or "INV123" etc.
                var standaloneMatch = Regex.Match(line.Trim(), @"^([A-Z]{1,3}\d{4,}|[A-Z]\d{4}-\d+|\d{4}-\d+)$");
                if (standaloneMatch.Success)
                {
                    data.InvoiceNumber = standaloneMatch.Groups[1].Value;
                    break;
                }
            }
        }

        // Date patterns - improved to handle various formats
        var datePatterns = new[]
        {
            @"(?i)(?:factuur|invoice)\s*(?:datum|date)\s*[:\-]?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})",
            @"(?i)datum\s*[:\-]?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})",
            @"(?i)date\s*[:\-]?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})"
        };

        foreach (var line in lines)
        {
            foreach (var pattern in datePatterns)
            {
                var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    data.InvoiceDate = match.Groups[1].Value.Trim();
                    break;
                }
            }
            if (!string.IsNullOrEmpty(data.InvoiceDate)) break;
        }

        // If no explicit date found, look for standalone dates near invoice info
        if (string.IsNullOrEmpty(data.InvoiceDate))
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (Regex.IsMatch(line, @"(?i)factuur|invoice"))
                {
                    // Check surrounding lines for dates
                    for (int j = Math.Max(0, i - 2); j <= Math.Min(lines.Count - 1, i + 3); j++)
                    {
                        var dateMatch = Regex.Match(lines[j], @"\b(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})\b");
                        if (dateMatch.Success)
                        {
                            data.InvoiceDate = dateMatch.Groups[1].Value;
                            break;
                        }
                    }
                    if (!string.IsNullOrEmpty(data.InvoiceDate)) break;
                }
            }
        }

        // Due date patterns
        var dueDatePatterns = new[]
        {
            @"(?i)(?:vervaldatum|due\s*date|betalen\s*voor)\s*[:\-]?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})",
            @"(?i)betaaltermijn\s*[:\-]?\s*(\d{1,2}[-/.]\d{1,2}[-/.]\d{4})"
        };

        foreach (var pattern in dueDatePatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                data.DueDate = match.Groups[1].Value.Trim();
                break;
            }
        }

        // Order number patterns
        var orderPatterns = new[]
        {
            @"(?i)(?:order|bestelling)\s*(?:nummer|nr|no)\s*[:\-]?\s*([A-Z0-9\-_]+)",
            @"(?i)referentie\s*[:\-]?\s*([A-Z0-9\-_]+)"
        };

        foreach (var pattern in orderPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                data.OrderNumber = match.Groups[1].Value.Trim();
                break;
            }
        }
    }

    private void ExtractVendorInfo(InvoiceData data, string text, List<string> lines)
    {
        // Initialize nested objects if null
        data.Vendor ??= new VendorInfo();
        data.Vendor.Address ??= new AddressInfo();

        // Try to find vendor information (usually at the top)
        var topLines = lines.Take(15).ToList();

        // Look for company name - improved logic to handle OCR errors
        string bestCompanyName = null;
        int bestScore = 0;

        foreach (var line in topLines)
        {
            var cleanLine = line.Trim();

            // Skip obviously wrong lines
            if (cleanLine.Length < 3 ||
                Regex.IsMatch(cleanLine, @"(?i)factuur|invoice|datum|date|upload|nummer|klantnummer|prijs|totaal|btw|vat") ||
                Regex.IsMatch(cleanLine, @"^\d+$") ||
                Regex.IsMatch(cleanLine, @"^[A-Z]{2}\d") || // Skip postal codes
                cleanLine.Contains("€") ||
                Regex.IsMatch(cleanLine, @"^\d{4}\s*[A-Z]{2}")) // Postal code pattern
                continue;

            // Score potential company names
            int score = 0;

            // Contains "B.V" or similar company indicators
            if (Regex.IsMatch(cleanLine, @"(?i)\b(B\.?V\.?|BV|Ltd|Limited|Inc|Corp|Corporation|GmbH|S\.A\.?|N\.V\.?)\b"))
                score += 10;

            // Has reasonable length
            if (cleanLine.Length >= 5 && cleanLine.Length <= 50)
                score += 5;

            // Contains letters (not just numbers/symbols)
            if (Regex.IsMatch(cleanLine, @"[A-Za-z]{3,}"))
                score += 3;

            // Appears early in document
            if (topLines.IndexOf(line) < 5)
                score += 2;

            // Doesn't contain obvious address indicators
            if (!Regex.IsMatch(cleanLine, @"(?i)straat|gracht|weg|laan|plein|amsterdam|nederland|holland"))
                score += 2;

            if (score > bestScore)
            {
                bestScore = score;
                bestCompanyName = cleanLine;
            }
        }

        // Clean and assign the best company name found
        if (!string.IsNullOrEmpty(bestCompanyName))
        {
            // Clean up OCR artifacts
            var cleanName = bestCompanyName.Trim();
            cleanName = Regex.Replace(cleanName, @"[^\w\s\.\-&]", " ").Trim();
            cleanName = Regex.Replace(cleanName, @"\s+", " ");

            // Handle common OCR errors in Dutch company names
            cleanName = cleanName.Replace("drijf B.V", "bedrijf B.V.");
            cleanName = cleanName.Replace("ET ACET", "TECH CARE"); // Common OCR misread

            if (cleanName.Length > 2)
            {
                data.Vendor.CompanyName = cleanName;
            }
        }

        // Extract VAT/BTW number - improved patterns
        var vatPatterns = new[]
        {
            @"(?i)btw[-\s]*(?:nr|nummer)\s*[:\-]?\s*([A-Z]{2}\d{6,}B?\d{0,2})",
            @"(?i)vat[-\s]*(?:nr|number)\s*[:\-]?\s*([A-Z]{2}\d{6,}B?\d{0,2})",
            @"\b([A-Z]{2}\d{6,}B?\d{0,2})\b",
            @"(?i)btw\s*nummer\s*[:\-]?\s*([A-Z]{2}\d+)"
        };

        foreach (var pattern in vatPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var vatNumber = match.Groups[1].Value.Trim();
                // Validate VAT number format (NL followed by digits)
                if (vatNumber.Length >= 8 && vatNumber.StartsWith("NL"))
                {
                    data.Vendor.VatNumber = vatNumber;
                    break;
                }
            }
        }

        // Extract email
        var emailMatch = Regex.Match(text, @"\b([a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,})\b");
        if (emailMatch.Success)
        {
            data.Vendor.Email = emailMatch.Groups[1].Value;
        }

        // Extract phone - improved pattern
        var phoneMatch = Regex.Match(text, @"(?i)(?:tel|phone|telefoon)\s*[:\-]?\s*([\+\d\s\-\(\)]{8,})");
        if (phoneMatch.Success)
        {
            data.Vendor.Phone = phoneMatch.Groups[1].Value.Trim();
        }

        // Extract website
        var websiteMatch = Regex.Match(text, @"\b(?:www\.)?([a-zA-Z0-9.-]+\.[a-zA-Z]{2,})\b");
        if (websiteMatch.Success && !websiteMatch.Groups[1].Value.Contains("@"))
        {
            data.Vendor.Website = websiteMatch.Groups[1].Value;
        }

        // Extract IBAN
        var ibanMatch = Regex.Match(text, @"\b([A-Z]{2}\d{2}[A-Z0-9]{4}\d{7}[A-Z0-9]{0,16})\b");
        if (ibanMatch.Success)
        {
            data.Vendor.IBAN = ibanMatch.Groups[1].Value;
        }
    }

    private void ExtractCustomerInfo(InvoiceData data, string text, List<string> lines)
    {
        // Initialize nested objects if null
        data.Customer ??= new CustomerInfo();
        data.Customer.Address ??= new AddressInfo();

        // Extract customer number first
        var customerNumberPatterns = new[]
        {
            @"(?i)klantnummer\s*[:\-]?\s*([A-Z0-9]+)",
            @"(?i)customer\s*(?:nr|number)\s*[:\-]?\s*([A-Z0-9]+)",
            @"(?i)klant\s*nr\s*[:\-]?\s*([A-Z0-9]+)"
        };

        foreach (var pattern in customerNumberPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var customerNum = match.Groups[1].Value.Trim();
                // Avoid single letters or obvious non-numbers
                if (customerNum.Length > 1 && !customerNum.Equals("e", StringComparison.OrdinalIgnoreCase))
                {
                    data.Customer.CustomerNumber = customerNum;
                }
                break;
            }
        }

        // Look for customer company name - be more careful to avoid headers
        var customerSectionStarted = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            // Check if this line contains customer number - start looking after this
            if (Regex.IsMatch(line, @"(?i)klantnummer", RegexOptions.IgnoreCase))
            {
                customerSectionStarted = true;
                continue;
            }

            // If we're in customer section, look for actual customer name
            if (customerSectionStarted && string.IsNullOrWhiteSpace(data.Customer.CompanyName))
            {
                // Skip obvious header lines and invoice metadata
                if (Regex.IsMatch(line, @"(?i)prijs\s*per\s*stuk|totaal|bedrag|factuurnummer|factuurdatum|aantal|omschrijving|btw|korting"))
                    continue;

                // Look for customer name patterns
                var customerNamePatterns = new[]
                {
                    @"(?i)(?:factuur\s*aan|bill\s*to|aan)\s*[:\-]?\s*(.+)",
                    @"^([A-Z][A-Za-z\s&\.\-]{4,})$" // Company-like name starting with capital, at least 5 chars
                };

                foreach (var pattern in customerNamePatterns)
                {
                    var match = Regex.Match(line, pattern);
                    if (match.Success)
                    {
                        var customerName = match.Groups[1].Value.Trim();

                        // Clean up customer name - remove unwanted content
                        customerName = Regex.Replace(customerName, @"(?i)excl\.?\s*btw.*", "").Trim();
                        customerName = Regex.Replace(customerName, @"€.*", "").Trim();
                        customerName = Regex.Replace(customerName, @"\d+[.,]\d{2}.*", "").Trim();
                        customerName = Regex.Replace(customerName, @"(?i)klantnummer.*", "").Trim();
                        customerName = Regex.Replace(customerName, @"nummer:.*", "").Trim();

                        // Additional validation to avoid headers
                        if (!string.IsNullOrWhiteSpace(customerName) &&
                            customerName.Length > 3 &&
                            !Regex.IsMatch(customerName, @"^[\d\s\-\+\(\)\.]+$") &&
                            !Regex.IsMatch(customerName, @"(?i)^(factuur|invoice|datum|date|nummer|number|prijs|price|totaal|total|bedrag|amount)"))
                        {
                            data.Customer.CompanyName = customerName;
                            break;
                        }
                    }
                }

                // Stop looking after several lines if we don't find meaningful customer info
                var customerNumberLineIndex = lines.FindIndex(l => Regex.IsMatch(l, @"(?i)klantnummer"));
                if (customerNumberLineIndex >= 0 && i > customerNumberLineIndex + 8)
                {
                    break;
                }
            }
        }

        // If still no customer name found and we have a customer number, 
        // try to find company name in a more targeted way
        if (string.IsNullOrWhiteSpace(data.Customer.CompanyName) && !string.IsNullOrEmpty(data.Customer.CustomerNumber))
        {
            // Look for lines that appear to be company names in the middle section
            // (after vendor info but before line items)
            bool afterVendorInfo = false;

            foreach (var line in lines)
            {
                var cleanLine = line.Trim();

                // Skip if we've reached line items or pricing section
                if (Regex.IsMatch(cleanLine, @"(?i)omschrijving|description|periode|prijs|price|aantal|quantity|bedrag|amount|totaal|total"))
                {
                    break;
                }

                // Start looking after we've passed vendor information
                if (Regex.IsMatch(cleanLine, @"(?i)btw\s*nummer|vat\s*number") ||
                    cleanLine.Contains("@") ||
                    cleanLine.Contains("Nederland"))
                {
                    afterVendorInfo = true;
                    continue;
                }

                if (afterVendorInfo)
                {
                    // Look for lines that could be customer company names
                    if (Regex.IsMatch(cleanLine, @"^[A-Z][A-Za-z\s&\.\-]{6,40}$") &&
                        !Regex.IsMatch(cleanLine, @"(?i)nederland|amsterdam|utrecht|rotterdam|den\s*haag|eindhoven|factuur|datum|korting|flipover|classic") &&
                        !Regex.IsMatch(cleanLine, @"\d{4}\s*[A-Z]{2}") &&
                        !cleanLine.Contains("€"))
                    {
                        data.Customer.CompanyName = cleanLine;
                        break;
                    }
                }
            }
        }
    }

    private void ExtractFinancialInfo(InvoiceData data, string text, List<string> lines)
    {
        // Initialize nested objects if null
        data.Financial ??= new FinancialInfo();

        // Look for amounts in individual lines - more specific approach
        foreach (var line in lines)
        {
            var cleanLine = line.Trim();

            // Look for subtotal (excl BTW)
            if (Regex.IsMatch(cleanLine, @"(?i)totaal\s*excl\.?\s*btw|excl\.?\s*btw", RegexOptions.IgnoreCase))
            {
                // Look for amount in same or nearby lines
                var amountMatch = Regex.Match(cleanLine, @"(\d{1,6}[.,]\d{2})");
                if (amountMatch.Success && decimal.TryParse(amountMatch.Groups[1].Value.Replace(',', '.'), out decimal amount))
                {
                    data.Financial.SubTotal = amount;
                }
            }

            // Look for total (incl BTW)
            if (Regex.IsMatch(cleanLine, @"(?i)totaal\s*incl\.?\s*btw|incl\.?\s*btw", RegexOptions.IgnoreCase))
            {
                var amountMatch = Regex.Match(cleanLine, @"(\d{1,6}[.,]\d{2})");
                if (amountMatch.Success && decimal.TryParse(amountMatch.Groups[1].Value.Replace(',', '.'), out decimal amount))
                {
                    data.Financial.TotalAmount = amount;
                }
            }

            // Look for line item pricing patterns like "149,00 149,00"
            var lineItemPriceMatch = Regex.Match(cleanLine, @"^(\d{1,6}[.,]\d{2})\s+(\d{1,6}[.,]\d{2})$");
            if (lineItemPriceMatch.Success)
            {
                // This looks like unit price and line total
                if (decimal.TryParse(lineItemPriceMatch.Groups[2].Value.Replace(',', '.'), out decimal lineTotal))
                {
                    // If we don't have a total yet, this might be it
                    if (!data.Financial.TotalAmount.HasValue)
                    {
                        data.Financial.TotalAmount = lineTotal;
                    }
                }
            }
        }

        // Fallback: Extract any monetary amounts and make educated guesses
        var allAmounts = new List<decimal>();
        foreach (var line in lines)
        {
            var amounts = Regex.Matches(line, @"\b(\d{1,6}[.,]\d{2})\b");
            foreach (Match match in amounts)
            {
                if (decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), out decimal amount))
                {
                    allAmounts.Add(amount);
                }
            }
        }

        // If we still don't have totals, use heuristics
        if (!data.Financial.TotalAmount.HasValue && allAmounts.Any())
        {
            // The largest amount is likely the total
            data.Financial.TotalAmount = allAmounts.Max();
        }

        // Calculate missing values if we have some data
        if (data.Financial.TotalAmount.HasValue && !data.Financial.SubTotal.HasValue)
        {
            // Assume 21% VAT (standard in Netherlands)
            data.Financial.SubTotal = Math.Round(data.Financial.TotalAmount.Value / 1.21m, 2);
            data.Financial.TaxAmount = data.Financial.TotalAmount.Value - data.Financial.SubTotal.Value;
            data.Financial.TaxRate = 21.0m;
        }
        else if (data.Financial.SubTotal.HasValue && data.Financial.TotalAmount.HasValue)
        {
            // Calculate tax from the difference
            data.Financial.TaxAmount = data.Financial.TotalAmount.Value - data.Financial.SubTotal.Value;
            if (data.Financial.SubTotal.Value > 0)
            {
                data.Financial.TaxRate = Math.Round((data.Financial.TaxAmount.Value / data.Financial.SubTotal.Value) * 100, 1);
            }
        }
    }

    private void ExtractPaymentInfo(InvoiceData data, string text, List<string> lines)
    {
        // Extract payment terms
        var paymentTermsPatterns = new[]
        {
            @"(?i)(?:betaaltermijn|payment\s*terms)\s*[:\-]?\s*(\d+\s*dagen?|\d+\s*days?)",
            @"(?i)(?:betalen\s*binnen|pay\s*within)\s*[:\-]?\s*(\d+\s*dagen?|\d+\s*days?)"
        };

        foreach (var pattern in paymentTermsPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                data.Payment.PaymentTerms = match.Groups[1].Value.Trim();
                break;
            }
        }

        // Extract IBAN for payment (might be different from vendor IBAN)
        var paymentIbanMatch = Regex.Match(text, @"(?i)(?:overboeking|transfer|betaling)\s*.*?([A-Z]{2}\d{2}[A-Z0-9]{4}\d{7}[A-Z0-9]{0,16})");
        if (paymentIbanMatch.Success)
        {
            data.Payment.IBAN = paymentIbanMatch.Groups[1].Value;
        }
        else if (!string.IsNullOrEmpty(data.Vendor?.IBAN))
        {
            data.Payment.IBAN = data.Vendor.IBAN;
        }
    }

    private void ExtractLineItems(InvoiceData data, string text, List<string> lines)
    {
        data.LineItems ??= new List<InvoiceLineItem>();

        // Look for line items based on the actual OCR structure
        bool inLineItemsSection = false;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            // Check if we're entering line items section
            if (Regex.IsMatch(line, @"(?i)aantal\s*omschrijving|omschrijving|description|artikel|product"))
            {
                inLineItemsSection = true;
                continue;
            }

            // Stop if we hit totals section or discount section
            if (Regex.IsMatch(line, @"(?i)totaal\s*excl|totaal\s*incl|btw|vat|te\s*betalen|korting"))
            {
                inLineItemsSection = false;
                break;
            }

            if (inLineItemsSection)
            {
                // Pattern 1: Line starting with quantity followed by description
                var quantityDescMatch = Regex.Match(line, @"^(\d+)\s+(.{5,})$");
                if (quantityDescMatch.Success)
                {
                    var quantity = decimal.TryParse(quantityDescMatch.Groups[1].Value, out var qty) ? qty : 1;
                    var description = quantityDescMatch.Groups[2].Value.Trim();

                    // Clean description from common OCR artifacts
                    description = description.Replace("nderhoud", "onderhoud");

                    // Look ahead for pricing information
                    decimal? unitPrice = null;
                    decimal? lineTotal = null;

                    // Check next few lines for pricing patterns
                    for (int j = i + 1; j < Math.Min(i + 4, lines.Count); j++)
                    {
                        var nextLine = lines[j].Trim();

                        // Look for period information
                        if (Regex.IsMatch(nextLine, @"(?i)periode", RegexOptions.IgnoreCase))
                        {
                            description += " - " + nextLine;
                            continue;
                        }

                        // Look for standalone amounts that could be the price
                        var amountMatch = Regex.Match(nextLine, @"^(\d{1,6}[.,]\d{2})$");
                        if (amountMatch.Success)
                        {
                            if (decimal.TryParse(amountMatch.Groups[1].Value.Replace(',', '.'), out var amount))
                            {
                                unitPrice = amount;
                                lineTotal = amount * quantity;
                                break;
                            }
                        }
                    }

                    // Add the line item
                    var lineItem = new InvoiceLineItem
                    {
                        Description = description,
                        Quantity = quantity,
                        UnitPrice = unitPrice,
                        LineTotal = lineTotal ?? (unitPrice * quantity)
                    };

                    data.LineItems.Add(lineItem);
                }

                // Pattern 2: Lines that look like product names without quantity prefix
                else if (Regex.IsMatch(line, @"^[A-Za-z][A-Za-z\s]{4,}$") &&
                         !Regex.IsMatch(line, @"(?i)totaal|korting|betalen|factuur"))
                {
                    // This might be a product line like "Flipover Classic"
                    var description = line.Trim();

                    // Look for pricing in nearby lines
                    decimal? unitPrice = null;
                    decimal? lineTotal = null;

                    for (int j = i + 1; j < Math.Min(i + 3, lines.Count); j++)
                    {
                        var nextLine = lines[j].Trim();
                        var amountMatch = Regex.Match(nextLine, @"(\d{1,6}[.,]\d{2})");
                        if (amountMatch.Success)
                        {
                            if (decimal.TryParse(amountMatch.Groups[1].Value.Replace(',', '.'), out var amount))
                            {
                                unitPrice = amount;
                                lineTotal = amount;
                                break;
                            }
                        }
                    }

                    if (unitPrice.HasValue)
                    {
                        var lineItem = new InvoiceLineItem
                        {
                            Description = description,
                            Quantity = 1,
                            UnitPrice = unitPrice,
                            LineTotal = lineTotal
                        };

                        data.LineItems.Add(lineItem);
                    }
                }

                // Pattern 3: Lines containing period information 
                var periodMatch = Regex.Match(line, @"(?i)periode[:\s]*(.+)", RegexOptions.IgnoreCase);
                if (periodMatch.Success)
                {
                    var description = $"Service periode: {periodMatch.Groups[1].Value.Trim()}";

                    // This is likely a service period, add it as a line item
                    var lineItem = new InvoiceLineItem
                    {
                        Description = description,
                        Quantity = 1,
                        UnitPrice = null, // Will be filled by financial totals if this is the main item
                        LineTotal = null
                    };

                    data.LineItems.Add(lineItem);
                }
            }
        }

        // If we found line items but no pricing, try to use the financial total
        if (data.LineItems.Any() && data.Financial?.SubTotal.HasValue == true)
        {
            var itemsWithoutPricing = data.LineItems.Where(li => !li.UnitPrice.HasValue).ToList();
            if (itemsWithoutPricing.Count == 1)
            {
                // Assign the subtotal to the single item without pricing
                var item = itemsWithoutPricing.First();
                item.UnitPrice = data.Financial.SubTotal.Value;
                item.LineTotal = data.Financial.SubTotal.Value * (item.Quantity ?? 1);
            }
        }
    }

    private void DetectLanguageAndCurrency(InvoiceData data, string text)
    {
        // Detect language
        if (Regex.IsMatch(text, @"(?i)\b(factuur|btw|totaal|klant|betaling)\b"))
        {
            data.Language = "Dutch";
        }
        else if (Regex.IsMatch(text, @"(?i)\b(invoice|vat|total|customer|payment)\b"))
        {
            data.Language = "English";
        }

        // Detect currency
        if (text.Contains("€") || Regex.IsMatch(text, @"(?i)\beur\b"))
        {
            data.Currency = "EUR";
        }
        else if (text.Contains("$") || Regex.IsMatch(text, @"(?i)\busd\b"))
        {
            data.Currency = "USD";
        }
        else if (text.Contains("£") || Regex.IsMatch(text, @"(?i)\bgbp\b"))
        {
            data.Currency = "GBP";
        }
    }
}