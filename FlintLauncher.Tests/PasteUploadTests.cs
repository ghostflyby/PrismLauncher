// SPDX-License-Identifier: GPL-3.0-only
// Tests for the PasteUpload port (launcher/net/PasteUpload.{h,cpp} + logs/AnonymizeLog.cpp):
// the provider table, the URL resolution (incl. the paste.gg api.paste.gg HACK), the exact
// request bodies of all four providers, the anonymization rules and the response parsing of
// every provider (success, provider error, malformed body, HTTP status gate).

using System.Globalization;
using System.Text;
using System.Text.Json;
using FlintLauncher.Core.Net;
using FlintLauncher.Core.Tasks;
using Xunit;

namespace FlintLauncher.Tests;

public class PasteUploadTests : IDisposable
{
    private readonly PeripheralStubServer _server = new();

    public void Dispose()
    {
        _server.Dispose();
    }

    private const string LogLine = "line one\nline two & stuff=ok\n";

    // ---------------------------------------------------------------- provider table

    [Fact]
    public void PasteTypes_Table_Matches_The_Cpp()
    {
        Assert.Equal(4, PasteUpload.PasteTypes.Length);
        Assert.Equal("0x0.st", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.NullPointer].Name);
        Assert.Equal("https://0x0.st", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.NullPointer].DefaultBase);
        Assert.Equal("", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.NullPointer].EndpointPath);

        Assert.Equal("hastebin", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.Hastebin].Name);
        Assert.Equal("https://hst.sh", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.Hastebin].DefaultBase);
        Assert.Equal("/documents", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.Hastebin].EndpointPath);

        Assert.Equal("paste.gg", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.PasteGg].Name);
        Assert.Equal("https://paste.gg", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.PasteGg].DefaultBase);
        Assert.Equal("/api/v1/pastes", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.PasteGg].EndpointPath);

        Assert.Equal("mclo.gs", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.Mclogs].Name);
        Assert.Equal("https://api.mclo.gs", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.Mclogs].DefaultBase);
        Assert.Equal("/1/log", PasteUpload.PasteTypes[(int)PasteUpload.PasteType.Mclogs].EndpointPath);
    }

    [Fact]
    public void Url_Resolution()
    {
        // empty url -> provider default base + endpoint path
        Assert.Equal("https://hst.sh/documents",
            new PasteUpload(LogLine, "", PasteUpload.PasteType.Hastebin).EndpointUrl);
        Assert.Equal("https://0x0.st",
            new PasteUpload(LogLine, "", PasteUpload.PasteType.NullPointer).EndpointUrl);
        Assert.Equal("https://api.mclo.gs/1/log",
            new PasteUpload(LogLine, "", PasteUpload.PasteType.Mclogs).EndpointUrl);

        // custom url -> base + endpoint path
        Assert.Equal(_server.BaseUrl + "/documents",
            new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.Hastebin).EndpointUrl);
        Assert.Equal(_server.BaseUrl,
            new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.NullPointer).EndpointUrl);

        // HACK: the official paste.gg instance does not follow its own documented api path
        Assert.Equal("https://api.paste.gg/v1/pastes",
            new PasteUpload(LogLine, "", PasteUpload.PasteType.PasteGg).EndpointUrl);
        // ... but a custom paste.gg base keeps the endpoint path suffix
        Assert.Equal(_server.BaseUrl + "/api/v1/pastes",
            new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.PasteGg).EndpointUrl);
    }

    // ---------------------------------------------------------------- AnonymizeLog.cpp

    [Fact]
    public void AnonymizeLog_Applies_All_Seven_Rules()
    {
        var log = new StringBuilder()
            .AppendLine(@"C:\Users\steve\AppData\Roaming\.minecraft\crash.txt")
            .AppendLine("C:/Users/Steve/AppData/caches/")
            .AppendLine("/home/alexander/.minecraft/logs/")
            .AppendLine("/Users/ghostflyby/Library/Logs/minecraft.log")
            .AppendLine("(Session ID is 5uper-secr3t-token)")
            .AppendLine("new refresh token: \"very-secret-value\"")
            .AppendLine("\"device_code\" :  \"0.ATRA-6_Y\"")
            .ToString();

        var anonymized = PasteUpload.AnonymizeLog(log);
        Assert.DoesNotContain("steve", anonymized);
        Assert.DoesNotContain("Steve", anonymized);
        Assert.DoesNotContain("alexander", anonymized);
        Assert.DoesNotContain("ghostflyby", anonymized);
        Assert.DoesNotContain("5uper-secr3t-token", anonymized);
        Assert.DoesNotContain("very-secret-value", anonymized);
        Assert.DoesNotContain("0.ATRA-6_Y", anonymized);

        Assert.Contains(@"C:\Users\********\AppData\Roaming\.minecraft\crash.txt", anonymized);
        Assert.Contains("C:/Users/********/AppData/caches/", anonymized);
        Assert.Contains("/home/********/.minecraft/logs/", anonymized);
        Assert.Contains("/Users/********/Library/Logs/minecraft.log", anonymized);
        Assert.Contains("(Session ID is <SESSION_TOKEN>)", anonymized);
        Assert.Contains("new refresh token: \"<TOKEN>\"", anonymized);
        Assert.Contains("\"device_code\" :  \"<DEVICE_CODE>\"", anonymized);
    }

    [Fact]
    public void AnonymizeLog_Rules_Are_Case_Insensitive()
    {
        Assert.Contains("C:\\Users\\********\\", PasteUpload.AnonymizeLog(@"c:\users\name\x"));
        Assert.Contains("(Session ID is <SESSION_TOKEN>)", PasteUpload.AnonymizeLog("(session id is abc)"));
    }

    [Fact]
    public void AnonymizeLog_Preserves_Unrelated_Text()
    {
        var log = "normal log line\n/home is a nice place\nnothing secret here\n";
        Assert.Equal(log, PasteUpload.AnonymizeLog(log));
    }

    // The C++ constructor anonymizes the log before upload.
    [Fact]
    public void Constructor_Anonymizes_The_Log()
    {
        var upload = new PasteUpload("welcome /home/alice/minecraft", "", PasteUpload.PasteType.Hastebin);
        Assert.Equal("welcome /home/********/minecraft", upload.Log);
    }

    // ---------------------------------------------------------------- request bodies

    [Fact]
    public async Task NullPointer_Sends_The_File_Multipart()
    {
        _server.Responder = _ => (200, "http://127.0.0.1:1/abcde\n", "OK");

        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.NullPointer);
        await upload.StartAsync();

        Assert.Equal(TaskState.Succeeded, upload.State);
        // the link is the trimmed response body
        Assert.Equal("http://127.0.0.1:1/abcde", upload.PasteLink);

        var request = _server.Request("/");
        Assert.NotNull(request);
        Assert.StartsWith("multipart/form-data; boundary=", request.Headers["Content-Type"]);
        var boundary = MultipartAssertions.BoundaryFromContentType(request.Headers["Content-Type"]);
        var parts = MultipartAssertions.SplitParts(request.Body, boundary);
        Assert.Single(parts);
        Assert.Equal(
        [
            "Content-Type: text/plain",
            "Content-Disposition: form-data; name=\"file\"; filename=\"log.txt\"",
        ], parts[0].Headers);
        Assert.Equal(LogLine, Encoding.UTF8.GetString(parts[0].Payload));
    }

    [Fact]
    public async Task Hastebin_Posts_The_Raw_Log_And_Builds_The_Link()
    {
        _server.Responder = _ => (200, """{"key":"61c6c1ad"}""", "OK");

        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.Hastebin);
        await upload.StartAsync();

        Assert.Equal(TaskState.Succeeded, upload.State);
        Assert.Equal(_server.BaseUrl + "/61c6c1ad", upload.PasteLink);

        var request = _server.Request("/documents");
        Assert.NotNull(request);
        Assert.Equal(LogLine, request.BodyAsString);
    }

    [Fact]
    public async Task Mclogs_Sends_UrlEncoded_Content()
    {
        _server.Responder = _ => (200, """{"success":true,"url":"https://mclo.gs/XYZ"}""", "OK");

        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.Mclogs);
        await upload.StartAsync();

        Assert.Equal(TaskState.Succeeded, upload.State);
        Assert.Equal("https://mclo.gs/XYZ", upload.PasteLink);

        var request = _server.Request("/1/log");
        Assert.NotNull(request);
        Assert.Equal("application/x-www-form-urlencoded", request.Headers["Content-Type"]);
        // QUrlQuery encoding: %20 for spaces, %0A for newlines, reserved chars escaped
        Assert.Equal("content=line%20one%0Aline%20two%20%26%20stuff%3Dok%0A", request.BodyAsString);
    }

    [Fact]
    public async Task PasteGG_Sends_The_Indented_Json_Document()
    {
        _server.Responder = _ => (200, """{"status":"success","result":{"id":"abcdef"}}""", "OK");

        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.PasteGg);
        await upload.StartAsync();

        Assert.Equal(TaskState.Succeeded, upload.State);
        Assert.Equal(_server.BaseUrl + "/p/anonymous/abcdef", upload.PasteLink);

        var request = _server.Request("/api/v1/pastes");
        Assert.NotNull(request);
        Assert.Equal("application/json", request.Headers["Content-Type"]);

        using var doc = JsonDocument.Parse(request.BodyAsString);
        var root = doc.RootElement;
        // expires = upload time + 100 days (Qt ISODate, UTC)
        var expires = DateTime.Parse(root.GetProperty("expires").GetString()!, null,
            DateTimeStyles.RoundtripKind);
        var delta = expires - DateTime.UtcNow;
        Assert.True(delta > TimeSpan.FromDays(99.9) && delta < TimeSpan.FromDays(100.1),
            "expires should be ~100 days out, got " + delta);
        var file = root.GetProperty("files")[0];
        Assert.Equal("log.txt", file.GetProperty("name").GetString());
        Assert.Equal("text", file.GetProperty("content").GetProperty("format").GetString());
        Assert.Equal(LogLine, file.GetProperty("content").GetProperty("value").GetString());

        // QJsonDocument::toJson() is Indented (4 spaces) by default — keep that shape
        var expectedPrefix = "{" + Environment.NewLine + "    \"expires\":";
        Assert.StartsWith(expectedPrefix, request.BodyAsString);
    }

    // ---------------------------------------------------------------- response parsing

    [Fact]
    public async Task Hastebin_Malformed_Response_Fails()
    {
        _server.Responder = _ => (200, """{"no_key":1}""", "OK");
        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.Hastebin);
        await upload.StartAsync();
        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Equal($"Error: {_server.BaseUrl}/documents returned a malformed response body", upload.FailReason);
    }

    [Fact]
    public async Task Mclogs_Provider_Error_Fails()
    {
        _server.Responder = _ => (200, """{"success":false,"error":"logs do not exist"}""", "OK");
        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.Mclogs);
        await upload.StartAsync();
        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Equal($"Error: {_server.BaseUrl}/1/log returned an error: logs do not exist", upload.FailReason);
    }

    [Fact]
    public async Task Mclogs_Malformed_Response_Fails()
    {
        _server.Responder = _ => (200, """{"unexpected":true}""", "OK");
        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.Mclogs);
        await upload.StartAsync();
        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Equal($"Error: {_server.BaseUrl}/1/log returned a malformed response body", upload.FailReason);
    }

    [Fact]
    public async Task PasteGG_Error_Code_Includes_Error_And_Message()
    {
        _server.Responder = _ => (200, """{"status":"error","error":"expired","message":"too old"}""", "OK");
        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.PasteGg);
        await upload.StartAsync();
        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Equal($"Error: {_server.BaseUrl}/api/v1/pastes returned an error code: expired\nError message: too old",
            upload.FailReason);
    }

    [Fact]
    public async Task PasteGG_Error_Code_Defaults_Message_To_None()
    {
        _server.Responder = _ => (200, """{"status":"error","error":"nope"}""", "OK");
        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.PasteGg);
        await upload.StartAsync();
        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Contains("Error message: none", upload.FailReason);
    }

    [Fact]
    public async Task Invalid_Json_Failures_Name_The_Provider()
    {
        _server.Responder = _ => (200, "garbage", "OK");

        foreach (var (type, provider) in new[]
                 {
                     (PasteUpload.PasteType.Hastebin, "hastebin"),
                     (PasteUpload.PasteType.Mclogs, "mclogs"),
                     (PasteUpload.PasteType.PasteGg, "pasteGG"),
                 })
        {
            var upload = new PasteUpload(LogLine, _server.BaseUrl, type);
            await upload.StartAsync();
            Assert.Equal(TaskState.Failed, upload.State);
            Assert.Contains($"Failed to parse response from {provider} server: expected JSON but got an invalid response.",
                upload.FailReason);
        }
    }

    [Fact]
    public async Task Non_2xx_Status_Fails_With_The_Cpp_Message()
    {
        _server.Responder = _ => (502, "", "Bad Gateway");
        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.Hastebin);
        await upload.StartAsync();
        Assert.Equal(TaskState.Failed, upload.State);
        Assert.Equal($"Error: {_server.BaseUrl}/documents returned unexpected status code 502 Bad Gateway",
            upload.FailReason);
    }

    // only 200 and 201 pass the gate (the C++ checks both explicitly)
    [Fact]
    public async Task Status_201_Passes_The_Gate()
    {
        _server.Responder = _ => (201, "https://0x0.st/ok\n", "Created");
        var upload = new PasteUpload(LogLine, _server.BaseUrl, PasteUpload.PasteType.NullPointer);
        await upload.StartAsync();
        Assert.Equal(TaskState.Succeeded, upload.State);
        Assert.Equal("https://0x0.st/ok", upload.PasteLink);
    }
}
