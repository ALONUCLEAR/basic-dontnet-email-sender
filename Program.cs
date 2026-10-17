using MailKit.Net.Smtp;
using MimeKit;
using MimeKit.Text;
using System.Text.Json.Serialization;
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

async Task<IResult> SendMail(MailInput input)
{
    try
    {
        Console.WriteLine($"Trying to send mail with input: {input}");

        if (!input.IsValid())
        {
            Console.WriteLine($"Invalid mail input: {input}");
            return Results.BadRequest("Invalid mail input");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", SENDER_EMAIL));
        message.To.AddRange(Map(input.Addresses, address => new MailboxAddress(address, address)));
        message.Subject = input.Subject;
        message.Body = new TextPart(TextFormat.Plain) { Text = input.Body };

        Console.WriteLine($"Connecting to SMTP server {SMTP_HOST}:{SMTP_PORT} and sending mail...");
        using (SmtpClient smptClient = new SmtpClient())
        {
            await smptClient.ConnectAsync(SMTP_HOST, SMTP_PORT, true);
            await smptClient.AuthenticateAsync(SENDER_EMAIL, SENDER_APP_PASSWORD);
            await smptClient.SendAsync(message);
        }

        Console.WriteLine($"Mail sent succesfully to {string.Join(", ", input.Addresses)}");
        return Results.Ok("Mail sent succesfully");
    } catch (Exception e) {
        Console.WriteLine($"Error sending mail: {e.Message}");
        return Results.Problem($"Error: {e.Message}", statusCode:StatusCodes.Status500InternalServerError);
        ;
    }
}
IResult validate(MailInput input)
{
    Console.WriteLine($"Validating input: {input}");
    string prefix = $"Given MailInput (subject={input.Subject}) is ";
    return input.IsValid()
        ? Results.Ok(prefix + "valid")
        : Results.BadRequest(prefix + "invalid");
}


app.MapPost("/send", (MailInput input) => SendMail(input));
app.MapPost("/validate", (MailInput input) => validate(input));
app.MapGet("/health", () => "Service is up and running!");

app.Run();

class MailInput
{
    [JsonPropertyName("addresses")]
    public string[] Addresses { get; set; } = Array.Empty<string>();

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    public MailInput() { }
    public MailInput(string[] addresses, string subject, string body)
    {
        Addresses = addresses;
        Subject = subject;
        Body = body;
    }

    public bool IsValid()
    {
        return Addresses?.Length > 0 && !string.IsNullOrEmpty(Subject);
    }

    public override string ToString()
    {
        string joinedAddresses = string.Join(", ", Addresses);
        return $"addresses: {joinedAddresses}\nsubject: {Subject}\nbody: {Body}";
    }
}