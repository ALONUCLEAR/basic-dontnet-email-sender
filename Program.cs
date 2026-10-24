using Serilog;
using Serilog.Sinks.OpenTelemetry;
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

string allAllowedAccessTokens = Environment.GetEnvironmentVariable("ALLOWED_ACCESS_TOKENS") ?? "";
string[] allowedAccessTokens = String.IsNullOrWhiteSpace(allAllowedAccessTokens)
    ? Array.Empty<string>()
    : allAllowedAccessTokens.Split(", ");
bool IsTokenValid(string token)
{
    return allowedAccessTokens.Contains(token);
}

try
{
    string axiomEndpoint = Environment.GetEnvironmentVariable("AXIOM_ENDPOINT") ?? builder.Configuration["AxiomEndpoint"] ?? "";
    string axiomToken = Environment.GetEnvironmentVariable("AXIOM_TOKEN") ?? builder.Configuration["AxiomToken"] ?? "";
    string axiomDataset = Environment.GetEnvironmentVariable("AXIOM_DATASET") ?? builder.Configuration["AxiomDataset"] ?? "";

    LoggerConfiguration loggerConfig = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console();

    if (!string.IsNullOrEmpty(axiomToken))
    {
        loggerConfig.WriteTo.OpenTelemetry(options =>
        {
            options.Endpoint = $"{axiomEndpoint}/v1/logs";
            options.Protocol = OtlpProtocol.HttpProtobuf;
            options.Headers = new Dictionary<string, string>
            {
                ["Authorization"] = axiomToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? axiomToken : $"Bearer {axiomToken}",
                ["X-Axiom-Dataset"] = axiomDataset
            };
        });
    }

    Log.Logger = loggerConfig.CreateLogger();
    builder.Host.UseSerilog();
    Log.Logger.Information("Logger setup completed successfully with Axiom!");
}
catch (Exception e)
{
    // can't log error with logger if the error IS from the logger
    Console.Error.WriteLine($"Error setting up logger: {e.Message}.\n\n{e.StackTrace}");
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(builder => builder.WithOrigins(allowedCorsWebsites)
        .AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseHttpsRedirection();

string[] UNREGULATED_PATHS = { "/health" };

app.Use(async (context, next) =>
{
    if (Array.Exists(UNREGULATED_PATHS, path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
    {
        await next();
        return;
    }

    string authHeaderKey = "Authorization";
    string authHeader = context.Request.Headers[authHeaderKey].ToString();
    string token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authHeader["Bearer ".Length..].Trim()
        : authHeader.Trim();

    if (string.IsNullOrEmpty(token) || !IsTokenValid(token))
    {
        await Results.Text(
            $"Either the '{authHeaderKey}' header is missing, or the token is not authorized for this request",
            statusCode: StatusCodes.Status401Unauthorized
        ).ExecuteAsync(context);
        return;
    }

    await next();
});

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