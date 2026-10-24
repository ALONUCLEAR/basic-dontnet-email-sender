using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using MailKit.Net.Smtp;
using MimeKit;
using MimeKit.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

string allAllowedWebsites = Environment.GetEnvironmentVariable("ALLOWED_CORS_WEBSITES") ?? "";
string[] allowedCorsWebsites = String.IsNullOrWhiteSpace(allAllowedWebsites)
    ? Array.Empty<string>()
    : allAllowedWebsites.Split(", ");

// use Serilog for logging, which allows us to connect to logging services like Loggly or Axiom.
// the logger reads the auth header from the environment variable Serilog__WriteTo__1__Args__headers__0
// and the dataset from Serilog__WriteTo__1__Args__headers__1, because of asp.net core
Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
builder.Host.UseSerilog();

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
int SMTP_TIMEOUT_SECONDS = int.Parse(Environment.GetEnvironmentVariable("SMTP_TIMEOUT_SECONDS") ?? "120");

TOut[] Map<TIn, TOut>(TIn[] input, Func<TIn, TOut> mapper)
{
    TOut[] result = new TOut[input.Length];
    for (int i = 0; i < input.Length; i++) result[i] = mapper(input[i]);
    return result;
}

async Task<IResult> SendMail(ILogger<Program> logger, MailInput input)
{
    string operationId = Guid.NewGuid().ToString();
    var LogInfoWithId = (string message) => logger.LogInfo(operationId, message);
    var LogErrorWithId = (string message) => logger.LogError(operationId, message);

    try
    {
        LogInfoWithId($"Trying to send mail with input: {input}");

        if (!input.IsValid())
        {
            LogErrorWithId($"Invalid mail input: {input}");
            return Results.BadRequest("Invalid mail input");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(SENDER_EMAIL, SENDER_EMAIL));
        message.To.AddRange(Map(input.Addresses, address => new MailboxAddress(address, address)));
        message.Subject = input.Subject;
        message.Body = new TextPart(TextFormat.Plain) { Text = input.Body };

        LogInfoWithId($"Connecting to SMTP server {SMTP_HOST}:{SMTP_PORT} and sending mail...");
        using (SmtpClient smptClient = new SmtpClient())
        {
            smptClient.Timeout = SMTP_TIMEOUT_SECONDS * 1000;
            await smptClient.ConnectAsync(SMTP_HOST, SMTP_PORT, true);
            await smptClient.AuthenticateAsync(SENDER_EMAIL, SENDER_APP_PASSWORD);
            await smptClient.SendAsync(message);
        }

        LogInfoWithId($"Mail sent succesfully to {string.Join(", ", input.Addresses)}");
        return Results.Ok("Mail sent succesfully");
    } catch (Exception e) {
        LogErrorWithId($"Error sending mail: {e.Message}");
        return Results.Problem($"Error: {e.Message}", statusCode:StatusCodes.Status500InternalServerError);
    }
}
IResult validate(ILogger<Program> logger, MailInput input)
{
    string operationId = Guid.NewGuid().ToString();
    logger.LogInfo(operationId, $"Validating input: {input}");
    string prefix = $"Given MailInput (subject={input.Subject}) is ";
    return input.IsValid()
        ? Results.Ok(prefix + "valid")
        : Results.BadRequest(prefix + "invalid");
}


app.MapPost("/send", (ILogger<Program> logger, MailInput input) => SendMail(logger, input));
app.MapPost("/validate", (ILogger<Program> logger, MailInput input) => validate(logger, input));
app.MapGet("/health", () => "Service is up and running!");

app.Run();

static class LoggerExtensions
{
    public static void LogInfo(this ILogger logger, string operationId, string message)
    {
        logger.LogInformation("[{OperationId}] {Message}", operationId, message);
    }

    public static void LogError(this ILogger logger, string operationId, string message)
    {
        logger.LogError("[{OperationId}] {Message}", operationId, message);
    }
}


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