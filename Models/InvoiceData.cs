public class InvoiceData
{
    // Basic Invoice Information
    public string? InvoiceNumber { get; set; }
    public string? InvoiceDate { get; set; }
    public string? DueDate { get; set; }
    public string? OrderNumber { get; set; }
    public string? Reference { get; set; }

    // Vendor/Supplier Information
    public VendorInfo? Vendor { get; set; }

    // Customer/Billing Information
    public CustomerInfo? Customer { get; set; }

    // Financial Information
    public FinancialInfo? Financial { get; set; }

    // Line Items
    public List<InvoiceLineItem>? LineItems { get; set; }

    // Payment Information
    public PaymentInfo? Payment { get; set; }

    // Additional Information
    public string? Currency { get; set; }
    public string? Language { get; set; }
    public string? Notes { get; set; }
    public List<string>? RawTextLines { get; set; }

    public InvoiceData()
    {
        LineItems = new List<InvoiceLineItem>();
        RawTextLines = new List<string>();
    }
}

public class VendorInfo
{
    public string? CompanyName { get; set; }
    public string? ContactPerson { get; set; }
    public AddressInfo? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? VatNumber { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? BankAccount { get; set; }
    public string? IBAN { get; set; }
}

public class CustomerInfo
{
    public string? CompanyName { get; set; }
    public string? ContactPerson { get; set; }
    public AddressInfo? Address { get; set; }
    public string? CustomerNumber { get; set; }
    public string? VatNumber { get; set; }
}

public class AddressInfo
{
    public string? Street { get; set; }
    public string? HouseNumber { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? FullAddress { get; set; }
}

public class FinancialInfo
{
    public decimal? SubTotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public decimal? TaxRate { get; set; }
    public string? TaxType { get; set; } // BTW, VAT, etc.
    public decimal? DiscountAmount { get; set; }
    public decimal? ShippingAmount { get; set; }
}

public class InvoiceLineItem
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? TaxRate { get; set; }
    public decimal? LineTotal { get; set; }
    public string? ProductCode { get; set; }
}

public class PaymentInfo
{
    public string? PaymentTerms { get; set; }
    public string? PaymentMethod { get; set; }
    public string? BankAccount { get; set; }
    public string? IBAN { get; set; }
    public string? BIC { get; set; }
    public string? PaymentReference { get; set; }
}