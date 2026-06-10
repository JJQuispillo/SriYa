using Qora.Billing.Domain.ValueObjects;

namespace Qora.Billing.Infrastructure.Email;

public class EmailMessage
{
    public string ToEmail { get; set; } = string.Empty;
    public string ToName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public List<EmailAttachment> Attachments { get; set; } = [];
    public EmailConfiguration Configuration { get; set; } = null!;
}

public class EmailAttachment
{
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public string ContentType { get; set; } = "application/octet-stream";
}
