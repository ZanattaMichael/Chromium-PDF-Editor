using PdfEditor.NativeHost;

// Chrome launches this host and speaks the native messaging protocol over stdio.
// Anything written to stdout must be a framed message, so diagnostics go to stderr.
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var processor = new MessageProcessor();
var assembler = new ChunkAssembler();

while (true)
{
    string? frame;
    try
    {
        frame = NativeMessaging.ReadMessage(stdin);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[pdf-editor-host] fatal read error: {ex.Message}");
        return 1;
    }
    if (frame == null) return 0; // browser closed the pipe

    string? request;
    try
    {
        request = assembler.Feed(frame);
    }
    catch (Exception ex)
    {
        NativeMessaging.WriteMessage(stdout,
            $"{{\"id\":\"\",\"ok\":false,\"result\":{{\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}}}");
        continue;
    }
    if (request == null) continue; // waiting for more chunks

    foreach (string response in processor.Handle(request))
        NativeMessaging.WriteMessage(stdout, response);
}
