using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class InvoiceController : ControllerBase
{
    private readonly IOcrService _ocrService;

    public InvoiceController(IOcrService ocrService)
    {
        _ocrService = ocrService;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadInvoice([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        var result = await _ocrService.ExtractTextFromImageAsync(file);
        return Ok(result);
    }
}
