// The "publisher-key" provisioning command, run instead of the service when it is the first argument. "generate"
// creates a new stream key and prints it once with its hash; "hash" reads an existing key from standard input (so it
// never appears in the process arguments or shell history) and prints only its hash.

using RTSP.IngestService.Configuration;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Hosting;

/// <summary>
/// Provisions publisher credentials for <c>Rtmp:Authentication:Publishers</c>.
/// </summary>
/// <remarks>
/// <code>
/// RTSP.IngestService publisher-key generate
/// RTSP.IngestService publisher-key hash &lt; key.txt
/// </code>
/// The command does not build the host, read configuration or write files. Output goes to standard output only.
/// </remarks>
public static class PublisherKeyCommand
{
    /// <summary>The command name.</summary>
    public const string CommandName = "publisher-key";

    /// <summary>Usage text, naming the executable.</summary>
    private static readonly string Usage =
        $"""
        Usage:
          {ApplicationIdentity.ServiceAssemblyName} publisher-key generate   Create a new stream key; prints the key once and its KeyHash.
          {ApplicationIdentity.ServiceAssemblyName} publisher-key hash       Read one stream key from standard input; prints its KeyHash.
        Store only the KeyHash in configuration. Give the stream key to the encoder operator and do not keep a copy.
        """;

    /// <summary>Runs the command if <paramref name="args"/> selects it.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="input">Standard input.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <param name="exitCode">The exit code when the command ran.</param>
    /// <returns><see langword="true"/> if the first argument is <see cref="CommandName"/> and the command ran.</returns>
    public static bool TryRun(string[] args, TextReader input, TextWriter output, TextWriter error, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        exitCode = ExitCodes.Success;
        if (args.Length == 0 || !string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var verb = args.Length == 2 ? args[1] : null;
        if (string.Equals(verb, "generate", StringComparison.OrdinalIgnoreCase))
        {
            var key = PublisherKeyHash.GenerateKey();
            output.WriteLine($"StreamKey: {key}");
            output.WriteLine($"KeyHash: {PublisherKeyHash.Create(key)}");
            return true;
        }

        if (string.Equals(verb, "hash", StringComparison.OrdinalIgnoreCase))
        {
            // Windows PowerShell 5.1 writes a UTF-8 byte order mark before text piped to a native command.
            var key = input.ReadLine()?.TrimStart('\uFEFF').Trim();
            if (key is null || !PublisherKeyHash.IsAcceptableKey(key))
            {
                error.WriteLine(
                    $"The stream key must be one line of {PublisherKeyHash.MinKeyLength} to {PublisherKeyHash.MaxKeyLength} printable ASCII characters without spaces or '?'.");
                exitCode = ExitCodes.UsageError;
                return true;
            }

            output.WriteLine($"KeyHash: {PublisherKeyHash.Create(key)}");
            return true;
        }

        error.WriteLine(Usage);
        exitCode = ExitCodes.UsageError;
        return true;
    }
}
