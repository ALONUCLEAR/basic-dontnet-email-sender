using MailKit.Net.Smtp;
using MimeKit;
using MimeKit.Text;
var builder = WebApplication.CreateBuilder(args);

string allAllowedWebsites = Environment.GetEnvironmentVariable("ALLOWED_CORS_WEBSITES") ?? "";
string[] allowedCorsWebsites = allAllowedWebsites.Split(", ");


builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(builder => builder.WithOrigins(allowedCorsWebsites)
        .AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseHttpsRedirection();

string SENDER_EMAIL = Environment.GetEnvironmentVariable("SENDER_EMAIL") ?? "";
/// <summary>
/// An app password is like an access token that email providers like gmail and outlook let us generate on our own and use instead of a password
/// </summary>
string SENDER_APP_PASSWORD = Environment.GetEnvironmentVariable("SENDER_APP_PASSWORD") ?? "";
string SMTP_HOST = Environment.GetEnvironmentVariable("SMTP_HOST") ?? "smtp.gmail.com";
int SMTP_PORT = int.Parse(Environment.GetEnvironmentVariable("SMTP_PORT") ?? "465");

TOut[] Map<TIn, TOut>(TIn[] input, Func<TIn, TOut> mapper)
{
    TOut[] result = new TOut[input.Length];
    for (int i = 0; i < input.Length; i++) result[i] = mapper(input[i]);
    return result;
}

async Task SendMail(MailInput input)
{
    var message = new MimeMessage();
    message.From.Add(new MailboxAddress("Sender", SENDER_EMAIL));
    message.To.AddRange(Map(input.emailAddresses, address => new MailboxAddress(address, address)));
    message.Subject = input.subject;
    message.Body = new TextPart(TextFormat.Plain) { Text = input.body };

    using (SmtpClient smptClient = new SmtpClient())
    {
        await smptClient.ConnectAsync(SMTP_HOST, SMTP_PORT, true);
        await smptClient.AuthenticateAsync(SENDER_EMAIL, SENDER_APP_PASSWORD);
        await smptClient.SendAsync(message);
    }
}

app.MapPost("/send", (MailInput input) => SendMail(input));
app.Run();

class MailInput
{
    public string[] emailAddresses = [];
    public string subject = "";
    public string body = "";

    public MailInput() { }
    public MailInput(string[] emailAddresses, string subject, string body)
    {
        this.emailAddresses = emailAddresses;
        this.subject = subject;
        this.body = body;
    }
}