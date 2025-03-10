using System.Net;
using System.Net.Mail;
using System.ServiceModel.Description;

public class EmailMessage
{
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsHtml { get; set; }
    public string[]? ToAddresses { get; set; }
    public string[]? CcAddresses { get; set; }
    public string[]? BccAddresses { get; set; }
    public List<EmailAttachment>? Attachments { get; set; }
}

public class EmailAttachment
{
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set;} = "application/octet-stream";
}

public class EmailService : IDisposable
{
    private readonly SmtpClient _smtpClient;

    public EmailService(string host,int port,string username,string password)
    {
        _smtpClient = new SmtpClient(host, port){
            EnableSsl = true,
            Credentials = new NetworkCredential(username,password)
        };
    }

    public async Task SendEmailAsync(EmailMessage message)
    {
        if(message.ToAddresses == null || !message.ToAddresses.Any())
            throw new ArgumentException("At least one recipient us required");

        using var mailMessage = new MailMessage{
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = message.IsHtml
        };
            
        foreach (var address in message.ToAddresses)
            mailMessage.To.Add(address);

        if (message.CcAddresses != null)
            foreach (var address in message.CcAddresses)
                mailMessage.CC.Add(address);

        if (message.BccAddresses != null)
            foreach (var address in message.BccAddresses)
                mailMessage.Bcc.Add(address);

        if (message.Attachments != null)
            foreach (var attachment in message.Attachments)
                mailMessage.Attachments.Add(new Attachment(
                    new MemoryStream(attachment.Content),
                    attachment.FileName,
                    attachment.ContentType
                ));

        await _smtpClient.SendMailAsync(mailMessage);
    }

    public void Dispose()
    {
        _smtpClient.Dispose();
    }

}